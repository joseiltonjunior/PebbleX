# pebbleX

Baseline arquitetural para um utilitário Windows voltado à futura sincronização do Easy-Switch entre periféricos Logitech. Esta Fase 1 não contém integração de hardware, HID, HID++, Bluetooth, WPF ou tray.

## Projetos

- `PebbleX.Core`: domínio e protocolo, independente de plataforma.
- `PebbleX.Windows`: futura integração Windows, dependente apenas de `PebbleX.Core`.
- `PebbleX.Cli`: harness de diagnóstico inicial, dependente de `PebbleX.Core` e `PebbleX.Windows`.

Os detalhes de arquitetura, segurança, protocolo, testes e referências estão em `docs/`.