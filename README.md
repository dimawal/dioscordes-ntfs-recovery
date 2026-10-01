# NtfsRecovery

Ferramenta de carving e recuperação de arquivos para volumes NTFS, com acesso **somente leitura** ao disco/imagem de origem. Varre um volume (ou imagem) em busca de registros MFT (`FILE`), reconstrói a árvore de diretórios virtual a partir deles — incluindo arquivos apagados — e permite inspecionar e recuperar arquivos individuais ou subárvores inteiras para um destino seguro.

## Como funciona

1. **`scan`** varre um intervalo de bytes do disco/imagem procurando a assinatura `FILE` e grava os registros MFT válidos (em uso ou apagados) em um banco SQLite.
2. **`tree`** / **`list`** / **`info`** reconstroem e navegam a árvore virtual de diretórios a partir desse banco, sem tocar no disco de origem novamente.
3. **`recover`** / **`extract-record`** usam o banco de scan + o disco/imagem original para extrair o conteúdo real dos arquivos (via data runs do `$DATA`) para um destino.

## Projetos

| Projeto | Descrição |
|---|---|
| `src/NtfsRecovery.Core` | Parsing do boot sector NTFS, registros MFT, atributos, data runs, scanner, reconstrução da árvore virtual, extração de arquivos e guardas de segurança. |
| `src/NtfsRecovery.Cli` | Interface de linha de comando (`System.CommandLine`) com os comandos abaixo. |
| `src/NtfsRecovery.Gui` | Interface gráfica WPF para navegar a árvore recuperada e disparar recuperações. |
| `tests/NtfsRecovery.Tests` | Testes xUnit do `Core`. |

## Requisitos

- .NET 8 SDK
- Windows, se for usar `--disk` (acesso a disco físico bruto) ou o projeto GUI (WPF)

## Build e testes

```bash
dotnet build
dotnet test
```

## Uso da CLI

```bash
dotnet run --project src/NtfsRecovery.Cli -- <comando> [opções]
```

Rodar sem argumentos imprime a ajuda completa de todos os comandos de uma vez.

### `inspect`
Lê e imprime os parâmetros do boot sector NTFS.
```bash
dotnet run --project src/NtfsRecovery.Cli -- inspect --image disco.img --offset 0
```

### `scan`
Varre uma imagem/disco por registros MFT carvados e persiste em um banco SQLite (permite retomar com `--resume`).
```bash
dotnet run --project src/NtfsRecovery.Cli -- scan --image disco.img --output scan.db
```

### `tree`
Reconstrói e imprime a árvore virtual de diretórios a partir do banco de scan.
```bash
dotnet run --project src/NtfsRecovery.Cli -- tree --scan scan.db --path "\Users\Mateus"
```

### `list`
Lista os filhos de um diretório virtual.
```bash
dotnet run --project src/NtfsRecovery.Cli -- list "\Users\Mateus\Documentos" --scan scan.db
```

### `info`
Mostra os metadados completos de um arquivo ou diretório (timestamps, tamanho, estado em-uso/apagado etc.).
```bash
dotnet run --project src/NtfsRecovery.Cli -- info "\Users\Mateus\foto.jpg" --scan scan.db
```

### `recover`
Recupera um arquivo ou uma subárvore inteira para um diretório de destino.
```bash
dotnet run --project src/NtfsRecovery.Cli -- recover "\Users\Mateus\Documentos" --scan scan.db --destination D:\Recuperado --image disco.img
```

### `extract-record`
Recupera um único arquivo diretamente pelo número do registro MFT.
```bash
dotnet run --project src/NtfsRecovery.Cli -- extract-record --record 12345 --scan scan.db --image disco.img --output D:\Recuperado\arquivo.jpg
```

## Interface gráfica (GUI)

O `NtfsRecovery.Gui` é um aplicativo WPF (Windows) que expõe o mesmo fluxo da CLI — carregar o volume, escanear, navegar a árvore e recuperar — em uma única janela, sem precisar usar o terminal.

```bash
dotnet run --project src/NtfsRecovery.Gui
```

Fluxo de uso:

1. **Origem**: informe o caminho de uma imagem (`--image`) ou marque "Usar disco físico" e informe o caminho do disco (ex.: `\\.\PhysicalDrive1`), além do offset da partição. Clique em **"Carregar informações do volume"** para ler o boot sector.
2. **Varredura**: defina (ou aceite a sugestão automática de) um caminho de banco de dados para salvar os resultados e clique em **"Escanear"**. A barra de progresso e o status mostram o andamento; é possível **cancelar** a qualquer momento — o progresso fica salvo em checkpoint. Opções avançadas permitem limitar o intervalo escaneado, ajustar o tamanho do bloco de leitura e retomar uma varredura anterior.
3. **"Carregar do BD (sem varrer)"** reconstrói a árvore instantaneamente a partir de um banco de dados já existente, sem tocar no disco de origem novamente (necessário apenas recarregar o volume de origem se for recuperar arquivos depois).
4. **Navegação**: a árvore de pastas recuperada aparece à esquerda; selecionando uma pasta, seu conteúdo (arquivos e subpastas, com tamanho, número do registro MFT e estado em-uso/apagado) aparece à direita, com pré-visualização de metadados do item selecionado.
5. **Recuperação**: escolha uma pasta de destino e clique em **"Recuperar"** para extrair a pasta/arquivo selecionado. A mesma verificação de segurança da CLI impede recuperar para o mesmo disco físico de origem.

Um painel de log na parte inferior registra cada ação relevante (volume carregado, varredura concluída, bloqueios de segurança, erros).

## Segurança

O código de origem (`--image` ou `--disk`) é aberto estritamente como leitura. Antes de qualquer escrita, `recover` e `extract-record` passam pelo `SafetyGate`, que recusa a operação se o destino estiver no mesmo disco físico do que está sendo recuperado — evitando sobrescrever o disco de origem durante a recuperação.

## Licença

A definir.
