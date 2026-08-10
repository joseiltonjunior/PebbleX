# Arquitetura

`PebbleX.Core` é independente de plataforma e não referencia Windows. Ele concentrará futuramente o domínio e os fatos confirmados de protocolo.

`PebbleX.Windows` implementará a integração Win32/HID e referencia apenas `PebbleX.Core`. A enumeração de collections HID fica nesse projeto: o Configuration Manager lista somente interfaces HID presentes, e handles temporários `SafeFileHandle` são abertos com device-query access (`dwDesiredAccess = 0`) exclusivamente para tentar consultar atributos e preparsed data. O CLI consome esse serviço e não contém P/Invoke.

WPF e o tray serão criados somente após uma prova real com hardware. As dependências devem apontar para dentro: nunca `Core` para `Windows`.