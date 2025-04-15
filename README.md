
# DeviceListenerChanged

`DeviceListenerChanged` — это библиотека, позволяющий отслеживать подключения и отключения USB-устройств, COM-портов и HID-устройств по заданным VID и PID.

## Основные компоненты

### `TargetVidPid`
Класс, представляющий собой целевой идентификатор устройства:
- `VID` — Vendor ID.
- `PID` — Product ID.

### `DevineInterface`
Определяет типы устройств, за которыми необходимо следить:
- USB (`GUID_DEVINTERFACE_USB_DEVICE`)
- COM-порты (`GUID_DEVINTERFACE_COMPORT`)
- HID-устройства (`GUID_DEVINTERFACE_HID`)

### `DeviceNotificationListener`
Главный класс для регистрации уведомлений и обработки событий:
- Регистрирует окно для получения уведомлений от системы.
- Обрабатывает события подключения и отключения устройств.
- Предоставляет события `DeviceMatchedConnected` и `DeviceMatchedDisconnected`.

## Пример использования

```csharp
var target = new TargetVidPid(0x1234, 0x5678);
var devInterface = new DevineInterface(true, false, true);

var listener = new DeviceNotificationListener(target, devInterface);
listener.DeviceMatchedConnected += () => Console.WriteLine("Устройство подключено");
listener.DeviceMatchedDisconnected += () => Console.WriteLine("Устройство отключено");
```

## Примечания
- Компонент предназначен для работы только на Windows.

## Nuget
- https://www.nuget.org/packages/DeviceListenerChanged/1.0.1