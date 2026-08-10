# Segurança

Invariantes do projeto:

- Não haverá acesso de rede em runtime nem telemetria.
- Não haverá execução de subprocessos, processos, PowerShell ou `cmd`.
- Não haverá sistema de plugins nem carregamento dinâmico de código.
- Elevação administrativa não será o comportamento padrão.
- Somente dispositivos HID Logitech serão futuramente considerados.
- Nenhuma escrita HID ocorrerá sem identificação explícita do dispositivo.
- Filas futuras deverão ser bounded.
- Operações futuras em background deverão aceitar cancellation.
- Autostart não será implementado durante o desenvolvimento inicial.