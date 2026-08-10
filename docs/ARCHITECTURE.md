# Arquitetura

`PebbleX.Core` é independente de plataforma e não referencia Windows. Ele concentrará futuramente o domínio e os fatos confirmados de protocolo.

`PebbleX.Windows` implementa a integração Win32/HID e referencia apenas `PebbleX.Core`. A enumeração de collections HID fica nesse projeto: SetupAPI enumera interfaces HID presentes, Configuration Manager fornece a topologia PnP, e handles temporários `SafeFileHandle` são abertos com device-query access (`dwDesiredAccess = 0`) exclusivamente para consultar atributos, strings HID e preparsed data. O CLI consome esse serviço e não contém P/Invoke.

O agrupamento collection → dispositivo físico usa somente `DEVPKEY_Device_ContainerId` quando o PnP o fornece. O `DeviceInstanceId` e o pai imediato são exibidos como evidência de topologia, mas não são usados como substitutos inferidos de Container ID.

WPF e o tray serão criados somente após uma prova real com hardware. As dependências devem apontar para dentro: nunca `Core` para `Windows`.