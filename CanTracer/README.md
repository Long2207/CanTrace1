# CanTracer — cấu trúc source (bản gộp hoàn chỉnh)

Multi-bus CAN/CAN FD monitor cho PEAK + Vector, kèm hệ thống testcase (.mtc).
Đây là bộ source đã gộp toàn bộ các version (v4→v8) thành 1 bản nhất quán.

## Cách dùng bộ này

1. Trong Visual Studio, **xóa hết** các file .cs/.xaml cũ trong project (giữ lại
   `app.manifest` nếu có), rồi copy toàn bộ cây thư mục này vào.
2. Cài NuGet **SharpCompress** (đã khai trong .csproj — chỉ cần Restore).
3. Sửa `HintPath` của `vxlapi_NET` trong .csproj cho khớp máy bạn.
4. Đặt `PCANBasic.dll` cạnh file .csproj.
5. Build → Rebuild.

## Cấu trúc thư mục

```
CanTracer/
├── App.xaml / App.xaml.cs        Khởi động app, khai báo converters toàn cục
├── Converters.cs                 BoolToVis, InvertBool (dùng trong XAML)
├── PCANBasic.cs                  P/Invoke wrapper cho PEAK driver
├── MainWindow.xaml / .cs         Cửa sổ chính: toolbar, sidebar testcase, grid trace, Send Messenger
├── CanTracer.csproj              Project file (SharpCompress + vxlapi_NET)
│
├── Models/                       ❶ DỮ LIỆU THUẦN (không logic UI)
│   ├── CanMessage.cs             1 frame CAN (Rx/Tx) hiển thị trên trace
│   ├── AggregatedMessage.cs      1 dòng/bus+ID trong bảng tổng hợp (+ SignalValue)
│   ├── CanBus.cs                 1 bus đã cấu hình (channel + DBC + trạng thái)
│   ├── DbcMessage.cs             1 message DBC (BO_) + encode payload
│   ├── DbcSignal.cs              1 signal DBC (SG_): decode/encode + value table
│   ├── TestCaseModels.cs         Model .mtc: MtcSignal/MtcMessage/MtcBusFile/MtcTestCase
│   └── TestCaseFile.cs           1 dòng .mtc trong sidebar
│
├── Services/                     ❷ LOGIC NGHIỆP VỤ (phần cứng, file, parse)
│   ├── ICanService.cs            Interface chung cho mọi loại card CAN
│   ├── PcanService.cs            Triển khai cho PEAK
│   ├── VectorService.cs          Triển khai cho Vector (polling RX)
│   ├── ChannelDiscovery.cs       Dò channel PEAK + Vector
│   ├── BusManager.cs             Quản lý các bus, định tuyến frame, decode DBC
│   ├── DbcParser.cs              Parse file .dbc (BO_ + SG_ + VAL_)
│   ├── ConfigStore.cs            Lưu/đọc config.json (các bus đã cấu hình)
│   ├── LogFolderManager.cs       Quản lý folder Logs/ (BLF)
│   ├── TestCaseFolder.cs         Quản lý folder TestCases/
│   ├── MtcStore.cs               Đọc/ghi file .mtc (7z/LZMA2 qua SharpCompress)
│   └── TestCaseSender.cs         Engine bắn testcase cyclic + alias tag→bus
│
├── ViewModels/                   ❸ TRUNG GIAN giữa Model và View (MVVM)
│   ├── MainViewModel.cs          VM chính: buses, trace, testcase, send messenger
│   ├── RelayCommand.cs           ICommand cho nút bấm
│   ├── CyclicSender.cs           Gửi frame định kỳ (Send by signal)
│   └── SendMessengerVm.cs        VM cho panel Send Messenger (decode value table)
│
└── Views/                        ❹ GIAO DIỆN (dialog)
    ├── SettingsDialog            Cấu hình bus: channel + DBC + Connect từng bus
    ├── SendByDbcDialog           Gửi 1 message theo signal (one-shot/cyclic)
    ├── TestCaseEditorDialog      Tạo/sửa testcase, lưu .mtc
    ├── MessagePickerDialog       Chọn message từ DBC khi thêm vào testcase
    └── AddMessageDialog          Thêm message vào Send Messenger
```

## Luồng dữ liệu chính

```
Phần cứng (PEAK/Vector)
   │  XL_Receive / CAN_Read
   ▼
ICanService (PcanService | VectorService)   ← Services/
   │  event FrameReceived(CanMessage)
   ▼
BusManager  (gắn tên bus + màu + decode DBC)
   │  event FrameReceived
   ▼
MainViewModel  (gom vào AggregatedMessage)   ← ViewModels/
   │  ObservableCollection<AggregatedMessage>
   ▼
MainWindow DataGrid  (hiển thị trace)         ← Views/
```

## Luồng bắn testcase

```
File .mtc (7z)
   │  MtcStore.Load
   ▼
MtcTestCase (BusFiles theo tag INFO/CHAS/PT...)
   │  TestCaseSender.ResolveBus (alias INFO→ICAN...)
   ▼
TestCaseSender  (encode signal qua DBC, gửi cyclic theo Cycle_time)
   │  BusManager.Send
   ▼
ICanService.Send → phần cứng
```

## Quy tắc tổ chức (để không loạn lại)

- **Models** chỉ chứa dữ liệu + logic thuần (encode/decode). Không tham chiếu UI.
- **Services** chứa logic nghiệp vụ. Không tham chiếu ViewModels/Views.
- **ViewModels** điều phối. Tham chiếu Models + Services. Không tham chiếu Views trực tiếp (dùng event để mở dialog).
- **Views** chỉ XAML + code-behind tối thiểu. Lấy dữ liệu qua binding tới ViewModel.

Khi thêm tính năng mới: xác định nó thuộc tầng nào, đặt file vào đúng folder đó.
```
```
