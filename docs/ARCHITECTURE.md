# Arquitetura

## Implementação atual

`PebbleX.Core` é independente de plataforma e não referencia Windows. Ele concentrará futuramente o domínio e os fatos confirmados de protocolo.

`PebbleX.Windows` implementa a integração Win32/HID e referencia apenas `PebbleX.Core`. A enumeração de collections HID fica nesse projeto: SetupAPI enumera interfaces HID presentes, Configuration Manager fornece a topologia PnP, e handles temporários `SafeFileHandle` são abertos com device-query access (`dwDesiredAccess = 0`) exclusivamente para consultar atributos, strings HID e preparsed data. O CLI consome esse serviço e não contém P/Invoke.

O agrupamento collection → dispositivo físico usa somente `DEVPKEY_Device_ContainerId` quando o PnP o fornece. O `DeviceInstanceId` e o pai imediato são exibidos como evidência de topologia, mas não são usados como substitutos inferidos de Container ID.

Após a prova de hardware, `PebbleX.Desktop` adiciona uma aplicação WPF com uma tela principal simples e um modo desenvolvedor para a bancada. A janela chama diretamente os serviços de `PebbleX.Windows`, sem subprocessos. `HidDiagnosticSession` amostra a presença da interface a cada segundo, mantém uma única leitura ativa, trata falhas de leitura e tenta reabrir somente o caminho escolhido. Não substitui a interface por outro dispositivo com VID/PID iguais. Parar ou fechar cancela a sessão e aguarda a liberação da leitura.

A UI recebe registros por uma fila limitada a 1.000 entradas, atualiza a tela a cada 100 ms e mantém até 1.000 linhas. Registros omitidos por excesso de tráfego são contados e sinalizados. Salvar exporta o conteúdo retido, somente por ação do usuário. O inicializador de desenvolvimento `Abrir PebbleX.cmd` compila e abre a aplicação; a aplicação em execução não inicia outros processos.

Com o Flow ativo, fechar a janela pelo X cancela o fechamento, oculta a janela e mostra um ícone de notificação; os listeners e monitores continuam ativos. O menu do ícone reabre a janela ou solicita o encerramento normal, que cancela e aguarda as operações. Minimizar usa o comportamento WPF padrão e mantém a janela na barra de tarefas. O ciclo de ocultar/reabrir/encerrar foi validado pelo usuário em execução real. As dependências devem apontar para dentro: nunca `Core` para `Windows`.

`HidHostQuery` abre a interface selecionada com acesso de leitura/escrita e revalida VID/PID e os descritores no próprio handle. Consultas resolvem os índices novamente, usam software ID `0xD`, ignoram respostas de outras aplicações/notificações e têm limite de 3 segundos. Escritas são limitadas a: (1) reporting analítico dos CIDs D1/D2/D3 no teclado B377, preservando remapeamento e demais flags; (2) `ChangeHost.setCurrentHost` manual no mouse B034, após consultar a quantidade e canal atuais e validar o destino. A troca não tem resposta esperada porque a conexão cai; a confirmação exige reconexão e consulta separada. Não há envio arbitrário de pacotes. Um semáforo nomeado por caminho impede duas consultas/escritas PebbleX simultâneas na mesma sessão Windows; isso não coordena outras aplicações HID++.

A bancada desabilita comandos HID enquanto há captura ou outra consulta/escrita em andamento. O encerramento real cancela e aguarda operações ativas. O canal exibido é a última consulta com horário, nunca uma indicação de acompanhamento contínuo. O usuário confirmou o comando manual do mouse e o ciclo Flow ponta a ponta em hardware nos dois sentidos entre dois PCs Windows.

O inventário visual combina interfaces HID ativas com os nós de serviço Bluetooth HID Logitech que o Configuration Manager marca como presentes. O segundo conjunto só dá visibilidade ao pareamento/serviço reconhecido pelo Windows: uma entrada sem interface HID não permite captura, consulta de canal nem comando de troca. Um periférico com interface HID ativa é mostrado apenas uma vez. A lista é refeita a cada cinco segundos quando a bancada está parada.

## Coordenação pela rede local — direção aprovada

O contrato de produto e o fluxo estão em [PRODUCT.md](PRODUCT.md). O teclado é a origem dos pedidos; o mouse acompanha o canal. `FlowPeerTransport` implementa TCP com frames limitados, HMAC, expiração, concorrência limitada e deduplicação em memória. `FlowPairingService` anuncia somente ID/nome/portas por UDP local, descobre candidatos e faz um acordo ECDH P-256 com código de verificação de oito dígitos; as duas instalações precisam aceitar o mesmo código. A chave é derivada com HKDF-SHA256, nunca enviada pela rede, e `FlowLinkSettingsStore` a protege com DPAPI do usuário atual. O vínculo manual permanece como alternativa.

Com Flow ativo nos dois PCs, `HidKeyboardArrivalMonitor` consulta o ChangeHost após a chegada do B377. A instalação envia o canal confirmado ao par; este valida a origem, consulta e comanda somente a interface B034 local. A instalação de destino monitora a chegada do mouse, consulta o canal e retorna a confirmação com o mesmo ID de transferência. O status sincronizado exige esse retorno autenticado. Eventos D1/D2/D3 não são usados como gatilho. O usuário confirmou em hardware o ciclo Flow nos dois sentidos e a descoberta/pareamento automático. Presença HID contínua e deduplicação persistente entre reinícios continuam pendentes.

Responsabilidades previstas:

- `PebbleX.Core`: identidades de instalações e grupos de periféricos, mapa de canais, observações de presença, política de acompanhamento e estado de transferência. Sem dependência do Windows ou da UI.
- Serviço de comunicação local, separado dos adaptadores HID: descoberta por UDP, pareamento ECDH com confirmação do usuário e transporte autenticado implementados para um par; presença liveness ainda falta.
- Adaptadores por sistema: descoberta de periféricos, confirmação de canal e comando de troca nos dispositivos conectados localmente. O adaptador Windows existente será estendido; Linux ainda precisa de implementação própria.
- `PebbleX.Desktop`: painel Flow simplificado, vínculo, ativação/desativação e estado confirmado, mantendo os controles de diagnóstico no modo desenvolvedor.

Uma instalação tem identidade estável própria, diferente de nome/IP e de endereço de dispositivo HID. Cada início do agente tem uma nova sessão. Anúncios de descoberta não são confirmação de confiança ou compatibilidade de sincronização: conferir versão do protocolo, suporte do adaptador e capacidades do periférico.

A chegada confirmada do teclado a um participante pode iniciar a transferência: esse participante avisa pela rede o agente que ainda está conectado ao mouse. O proprietário atual do mouse valida o grupo e o destino e envia o comando local. O destino confirma o mouse antes de publicar o estado sincronizado. A notificação analítica do teclado será uma possível antecipação do mesmo fluxo, se comprovada no hardware.

Cada transferência precisa de ID, estado de origem esperado, destino e validade limitada. Processamento serial por grupo; pedidos repetidos devem ser idempotentes. Não ordenar observações distribuídas apenas pelo relógio de parede dos PCs. Para propostas conflitantes, perda de sessão, presença ambígua ou expiração, interromper a decisão automática e obter estado recente dos participantes antes de comandar novamente. A arbitragem entre mais de dois participantes é um gate próprio, não uma propriedade assumida do primeiro teste com dois PCs.

Intervenção manual no mouse não dispara o fluxo inverso nem uma correção contínua. Desativar a sincronização bloqueia novos envios; desligar só a captura de diagnóstico não equivale a desligar o coordenador. Estado offline ou desconhecido nunca é apresentado como sincronizado.
