# Plano: detectar e listar partições (em vez de digitar o offset manualmente)

Status: planejado, não implementado ainda.

## Problema atual

Hoje, tanto a CLI (`inspect --offset`, `scan --partition-offset`) quanto a GUI (campo de texto "Offset da partição (bytes)" em `MainViewModel.PartitionOffsetText` / `MainWindow.xaml`) exigem que o usuário já saiba o byte-offset onde a partição NTFS começa. Não existe nenhuma leitura da tabela de partições do disco — o usuário precisa descobrir o offset por fora (ex. com outra ferramenta) e digitá-lo.

## Objetivo

Ler a tabela de partições do disco/imagem (MBR e/ou GPT), listar as partições encontradas e, para cada uma, tentar validar se é NTFS (reaproveitando `NtfsBootSector.Parse`). O usuário escolhe a partição da lista em vez de digitar o offset.

## Decisão pendente: GPT só, ou GPT + MBR?

- **GPT only**: mais simples, cobre a maioria dos discos modernos (GPT é o padrão desde Windows Vista+/discos >2TB).
- **GPT + MBR**: cobre também discos/pendrives antigos formatados em MBR, inclusive partições estendidas/lógicas (mais código: parsing de uma tabela de partições aninhada).

Recomendação: começar com GPT, adicionar MBR em uma segunda etapa se necessário — mas decidir antes de começar.

## Diferença MBR vs GPT (referência rápida)

- **MBR**: setor 0 (512 bytes), 4 entradas de 16 bytes cada (partições primárias; uma pode ser "estendida" e encadear partições lógicas). Offsets/tamanhos em 32 bits (limite ~2 TiB). Tipo de partição é 1 byte, ambíguo (ex. `0x07` = NTFS ou exFAT). Sem checksum nem backup.
- **GPT**: setor 0 tem um MBR "protetor" (compatibilidade), a tabela real começa no LBA 1 (header) e aponta para até 128 entradas (padrão). Offsets/tamanhos em 64 bits. GUID de tipo inequívoco (ex. Microsoft Basic Data) + nome Unicode opcional. Tem CRC32 do header/tabela e uma cópia backup no fim do disco.

## Abordagem técnica

Novo namespace `NtfsRecovery.Core.Partitioning`, lendo sempre via `IBlockDevice.ReadAt` (somente leitura, mesmo padrão já usado por `RawDiskReader`/`ImageFileReader`):

1. `PartitionEntry` (record): `Offset`, `SizeInBytes`, `TypeDescription`, `Name` (opcional, GPT), `IsConfirmedNtfs` (preenchido depois de tentar `NtfsBootSector.Parse` no offset).
2. `GptPartitionReader`: lê o header GPT no LBA 1 (detecta pela assinatura `"EFI PART"`), localiza a tabela de entradas e decodifica GUID de tipo + nome UTF-16 de cada entrada.
3. (Se decidido incluir) `MbrPartitionReader`: lê as 4 entradas em `0x1BE` do setor 0; segue a cadeia de partições estendidas se houver.
4. `PartitionTableReader.Read(IBlockDevice device)`: detecta o esquema (checa assinatura GPT; senão assume MBR), delega ao reader correto, retorna a lista de `PartitionEntry`.
5. Para cada entrada retornada, tentar `NtfsBootSector.Parse` no seu offset (igual ao `try/catch InvalidNtfsBootSectorException` já usado em `InspectCommand`/`ScanCommand`) para marcar `IsConfirmedNtfs` e descartar/sinalizar as que não são NTFS.

## CLI

Novo comando `list-partitions` (mesmo padrão dos demais em `src/NtfsRecovery.Cli/Commands/`):

```
dotnet run --project src/NtfsRecovery.Cli -- list-partitions --image disco.img
```

Imprime uma tabela: índice, offset, tamanho, tipo, nome (se houver), se é NTFS confirmado. O offset impresso pode ser copiado direto para `--partition-offset`/`--offset` — ou, como melhoria, aceitar `--partition-index` nos outros comandos como alternativa ao offset manual.

## GUI

Em `MainViewModel`/`MainWindow.xaml`, junto ao botão "Carregar informações do volume":

1. Novo botão "Listar partições" (ou disparar automaticamente ao carregar o volume) que chama `PartitionTableReader` e popula uma nova `ObservableCollection<PartitionEntry>`.
2. Um `ComboBox`/`DataGrid` com as partições encontradas; ao selecionar uma, `PartitionOffsetText` é preenchido automaticamente (mantém o campo de texto como alternativa manual para casos não detectados).

## Testes

`tests/NtfsRecovery.Tests/Partitioning/`, seguindo o padrão de `TestSupport/InMemoryBlockDevice.cs` já usado nos testes de scan: montar buffers sintéticos de MBR e GPT em memória e verificar que `PartitionTableReader` extrai os offsets/tamanhos/tipos corretos, incluindo casos de tabela corrompida ou sem partições NTFS.

## Fora de escopo (por enquanto)

- Discos dinâmicos do Windows (LDM) e RAID por software.
- Partições NTFS dentro de containers (BitLocker, VHD/VHDX aninhados).
