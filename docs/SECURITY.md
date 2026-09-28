# Segurança

Invariantes do projeto:

- A comunicação funciona apenas na rede local; não depende de serviço em nuvem nem de internet. A descoberta usa anúncio UDP `47630` com identidade/nome/portas, sem chave ou comando. O pareamento e o Flow compartilham TCP `47631` em modos exclusivos.
- Descoberta não concede confiança. O pareamento troca chaves públicas ECDH P-256 efêmeras, deriva uma chave com HKDF-SHA256 e mostra um código de verificação de oito dígitos. As duas pessoas precisam comparar e aceitar o mesmo código; as mensagens de consentimento e decisão têm HMAC. A chave derivada não é enviada pela rede e fica protegida por DPAPI no perfil do usuário. O vínculo manual continua disponível como alternativa.
- Após o pareamento, pedidos do Flow são autenticados por HMAC-SHA256 com chave de pelo menos 256 bits, validade curta, tamanho limitado e proteção contra repetição. A deduplicação está na memória; reiniciar o processo encerra o listener e limpa pendências.
- A rede transportará presença, capacidades, canais e confirmações. Não transportará teclas digitadas, movimento do cursor ou conteúdo da área de transferência.
- Pedidos terão identificador, validade limitada e proteção contra repetição. Dados recebidos não poderão fornecer caminhos HID arbitrários nem pacotes HID brutos para execução.
- Não haverá execução de subprocessos, processos, PowerShell ou `cmd`.
- Não haverá sistema de plugins nem carregamento dinâmico de código.
- Elevação administrativa não será o comportamento padrão.
- Somente dispositivos HID Logitech serão futuramente considerados.
- Nenhuma escrita HID ocorrerá sem identificação explícita do dispositivo.
- Filas futuras deverão ser bounded.
- Operações futuras em background deverão aceitar cancellation.
- Autostart não será implementado durante o desenvolvimento inicial.
