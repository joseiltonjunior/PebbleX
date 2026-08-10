# Arquitetura

`PebbleX.Core` é independente de plataforma e não referencia Windows. Ele concentrará futuramente o domínio e os fatos confirmados de protocolo.

`PebbleX.Windows` implementará futuramente a integração Win32/HID e referencia apenas `PebbleX.Core`. `PebbleX.Cli` será inicialmente o harness de diagnóstico e pode referenciar ambos.

WPF e o tray serão criados somente após uma prova real com hardware. As dependências devem apontar para dentro: nunca `Core` para `Windows`.