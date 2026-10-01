using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Ntfs;
using NtfsRecovery.Core.Recovery;
using NtfsRecovery.Core.Safety;
using NtfsRecovery.Core.Scan;

namespace NtfsRecovery.Gui.ViewModels;

/// <summary>Synchronous IProgress -- System.Progress&lt;T&gt; marshals via the captured SynchronizationContext, which is unwanted for the inner, background-thread-only checkpoint bookkeeping in <see cref="MainViewModel"/>.</summary>
internal sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

/// <summary>
/// All NTFS/recovery logic here is delegated to NtfsRecovery.Core; this class only
/// orchestrates UI state, background execution, and binds the results for display. The
/// GUI never parses NTFS structures itself. User-facing strings are in Portuguese;
/// NTFS/engine-specific technical terms (MFT, $DATA, Healthy/Partial/Unsupported/
/// CorruptRuns/MetadataOnly, in-use/deleted, Non-resident, data runs) are kept in
/// English on purpose, matching the CLI and the project's internal vocabulary.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private const int BatchFlushSize = 5000;
    private const long CheckpointIntervalBytes = 64L * 1024 * 1024;
    private const int DefaultBlockSize = 16 * 1024 * 1024;

    private IBlockDevice? _device;
    private NtfsBootSector? _bootSector;
    private string _sourceDescription = "";
    private CancellationTokenSource? _scanCts;

    private string _imagePath = "";
    public string ImagePath { get => _imagePath; set => SetField(ref _imagePath, value); }

    private string _diskPath = @"\\.\PhysicalDrive1";
    public string DiskPath { get => _diskPath; set => SetField(ref _diskPath, value); }

    private bool _useDisk;
    public bool UseDisk { get => _useDisk; set => SetField(ref _useDisk, value); }

    private string _partitionOffsetText = "0";
    public string PartitionOffsetText { get => _partitionOffsetText; set => SetField(ref _partitionOffsetText, value); }

    private string _volumeInfoText = "(nenhum volume carregado)";
    public string VolumeInfoText { get => _volumeInfoText; set => SetField(ref _volumeInfoText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    private double _scanProgressPercent;
    public double ScanProgressPercent { get => _scanProgressPercent; set => SetField(ref _scanProgressPercent, value); }

    private string _scanStatusText = "Ocioso.";
    public string ScanStatusText { get => _scanStatusText; set => SetField(ref _scanStatusText, value); }

    public ObservableCollection<TreeItemViewModel> RootItems { get; } = [];

    private TreeItemViewModel? _selectedTreeItem;
    public TreeItemViewModel? SelectedTreeItem
    {
        get => _selectedTreeItem;
        set
        {
            if (SetField(ref _selectedTreeItem, value))
                RefreshFileList();
        }
    }

    public ObservableCollection<FileListItemViewModel> FileListItems { get; } = [];

    private FileListItemViewModel? _selectedFileItem;
    public FileListItemViewModel? SelectedFileItem
    {
        get => _selectedFileItem;
        set
        {
            if (SetField(ref _selectedFileItem, value))
                MetadataPreviewText = BuildMetadataPreview(value?.Node);
        }
    }

    private string _metadataPreviewText = "";
    public string MetadataPreviewText { get => _metadataPreviewText; set => SetField(ref _metadataPreviewText, value); }

    private string _destinationPath = "";
    public string DestinationPath { get => _destinationPath; set => SetField(ref _destinationPath, value); }

    private string _scanDbPath = "";
    public string ScanDbPath { get => _scanDbPath; set => SetField(ref _scanDbPath, value); }

    // -- Parâmetros avançados (equivalentes às opções da CLI) --
    private string _startOffsetText = "";
    public string StartOffsetText { get => _startOffsetText; set => SetField(ref _startOffsetText, value); }

    private string _lengthText = "";
    public string LengthText { get => _lengthText; set => SetField(ref _lengthText, value); }

    private string _blockSizeText = DefaultBlockSize.ToString();
    public string BlockSizeText { get => _blockSizeText; set => SetField(ref _blockSizeText, value); }

    private bool _resumeScan;
    public bool ResumeScan { get => _resumeScan; set => SetField(ref _resumeScan, value); }

    public ObservableCollection<string> LogEntries { get; } = [];

    public ICommand BrowseImageCommand { get; }
    public ICommand LoadVolumeCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand RecoverSelectedCommand { get; }
    public ICommand BrowseScanDbCommand { get; }
    public ICommand LoadFromDatabaseCommand { get; }

    public MainViewModel()
    {
        BrowseImageCommand = new RelayCommand(_ => BrowseImage());
        LoadVolumeCommand = new RelayCommand(_ => LoadVolume());
        ScanCommand = new RelayCommand(_ => _ = RunScanAsync(), _ => !IsBusy);
        CancelScanCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsBusy);
        BrowseDestinationCommand = new RelayCommand(_ => BrowseDestination());
        RecoverSelectedCommand = new RelayCommand(_ => _ = RecoverSelectedAsync(), _ => !IsBusy && SelectedTreeItem is not null);
        BrowseScanDbCommand = new RelayCommand(_ => BrowseScanDb());
        LoadFromDatabaseCommand = new RelayCommand(_ => _ = LoadFromDatabase(), _ => !IsBusy);
    }

    private void Log(string message) => LogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    private void BrowseImage()
    {
        var dialog = new OpenFileDialog { Filter = "Imagens de disco (*.img;*.dd;*.raw)|*.img;*.dd;*.raw|Todos os arquivos (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
            ImagePath = dialog.FileName;
    }

    private void BrowseDestination()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DestinationPath = dialog.SelectedPath;
    }

    private void BrowseScanDb()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Banco de dados de varredura (*.db)|*.db|Todos os arquivos (*.*)|*.*",
            FileName = Path.GetFileName(ScanDbPath),
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog() == true)
            ScanDbPath = dialog.FileName;
    }

    private static string SuggestScanDbPath(string sourceDescription)
    {
        string safeName = string.Join("_", sourceDescription.Split(Path.GetInvalidFileNameChars()));
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NtfsRecovery");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{safeName}.db");
    }

    /// <summary>Rebuilds the tree from a previously saved scan database in seconds, without touching the source device at all.</summary>
    private async Task LoadFromDatabase()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Banco de dados de varredura (*.db)|*.db|Todos os arquivos (*.*)|*.*",
            InitialDirectory = string.IsNullOrWhiteSpace(ScanDbPath) ? null : Path.GetDirectoryName(ScanDbPath),
        };
        if (dialog.ShowDialog() != true)
            return;

        await LoadFromDatabaseFile(dialog.FileName);
    }

    private async Task LoadFromDatabaseFile(string path)
    {
        IsBusy = true;
        ScanStatusText = "Carregando do banco de dados...";
        RootItems.Clear();
        FileListItems.Clear();

        try
        {
            TreeBuildResult treeResult = await Task.Run(() =>
            {
                using ScanStore store = ScanStore.OpenOrCreate(path);
                List<ScanRecordDto> raw = store.LoadAllRecords();
                List<ScanRecordDto> resolved = ScanRecordAttributeListResolver.Resolve(raw);

                var index = new VirtualMftIndex();
                foreach (ScanRecordDto dto in resolved)
                    index.AddDto(dto);

                return NtfsTreeBuilder.Build(index);
            });

            RootItems.Add(new TreeItemViewModel(treeResult.Root));
            SelectedTreeItem = RootItems[0];

            ScanDbPath = path;
            ScanStatusText =
                $"Carregado do banco de dados (nenhuma varredura realizada). Orphans: {treeResult.OrphanCount}, " +
                $"Noname: {treeResult.NonameCount}, Correspondências aproximadas: {treeResult.SoftMatchCount}";
            Log($"Árvore carregada de {path} sem escanear. Para recuperar arquivos, carregue também o volume de origem correspondente (mesma imagem/disco + offset usados naquela varredura).");
        }
        catch (Exception ex)
        {
            ScanStatusText = "Falha ao carregar o banco de dados.";
            Log($"ERRO ao carregar banco de dados: {ex.Message}");
            MessageBox.Show(ex.Message, "Falha ao carregar o banco de dados", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadVolume()
    {
        if (!long.TryParse(PartitionOffsetText, out long partitionOffset) || partitionOffset < 0)
        {
            MessageBox.Show("O offset da partição deve ser um número inteiro não negativo.", "Entrada inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _device?.Dispose();
        _device = null;
        _bootSector = null;

        try
        {
            if (UseDisk)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("O acesso a discos físicos exige Windows.");
                _device = new RawDiskReader(DiskPath);
                _sourceDescription = DiskPath;
            }
            else
            {
                _device = new ImageFileReader(ImagePath);
                _sourceDescription = ImagePath;
            }

            Span<byte> sector = stackalloc byte[NtfsBootSector.RawSize];
            if (_device.ReadAt(partitionOffset, sector) < NtfsBootSector.RawSize)
                throw new IOException("Não foi possível ler um boot sector completo nesse offset.");

            _bootSector = NtfsBootSector.Parse(sector);

            VolumeInfoText =
                $"Bytes/setor: {_bootSector.BytesPerSector}   " +
                $"Bytes/cluster: {_bootSector.BytesPerCluster}   " +
                $"Tamanho do registro MFT: {_bootSector.MftRecordSize}   " +
                $"Tamanho do volume: {_bootSector.VolumeSizeInBytes:N0} bytes";

            if (string.IsNullOrWhiteSpace(ScanDbPath))
                ScanDbPath = SuggestScanDbPath(_sourceDescription);

            Log($"Volume carregado: {_sourceDescription} @ offset {partitionOffset}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidNtfsBootSectorException or PlatformNotSupportedException)
        {
            VolumeInfoText = "(falha ao carregar o volume)";
            Log($"ERRO ao carregar volume: {ex.Message}");
            MessageBox.Show(ex.Message, "Falha ao carregar o volume", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunScanAsync()
    {
        if (_device is null || _bootSector is null)
        {
            MessageBox.Show("Carregue um volume primeiro.", "Nenhum volume carregado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long.TryParse(PartitionOffsetText, out long partitionOffset);

        long requestedStart = long.TryParse(StartOffsetText, out long so) ? so : partitionOffset;
        long requestedLength = long.TryParse(LengthText, out long len)
            ? len
            : _bootSector.VolumeSizeInBytes - (requestedStart - partitionOffset);
        long requestedEnd = requestedStart + requestedLength;
        int blockSize = int.TryParse(BlockSizeText, out int bs) && bs > 0 ? bs : DefaultBlockSize;
        bool resumeRequested = ResumeScan;

        IsBusy = true;
        ScanProgressPercent = 0;
        ScanStatusText = "Escaneando...";
        RootItems.Clear();
        FileListItems.Clear();
        _scanCts = new CancellationTokenSource();

        string? dbPath = string.IsNullOrWhiteSpace(ScanDbPath) ? null : ScanDbPath;
        if (dbPath is not null)
            Log($"Salvando os resultados da varredura em {dbPath} durante a execução, para nunca precisar escanear de novo.");
        else
            Log("AVISO: nenhum caminho de banco de dados definido -- os resultados só existirão em memória e serão perdidos se o aplicativo fechar.");

        IProgress<ScanProgress> progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressPercent = p.TotalBytes == 0 ? 0 : (double)p.BytesScanned / p.TotalBytes * 100.0;
        });

        try
        {
            IBlockDevice device = _device;
            NtfsBootSector bootSector = _bootSector;
            CancellationToken token = _scanCts.Token;

            ScanStatistics stats = await Task.Run(() =>
            {
                ScanStore? store = null;
                long effectiveStart = requestedStart;
                long lastCheckpointOffset = requestedStart;
                var pendingBatch = new List<ScanRecordDto>(BatchFlushSize);

                void FlushAndCheckpoint(long upToOffset)
                {
                    if (store is null)
                        return;
                    if (pendingBatch.Count > 0)
                    {
                        store.InsertRecords(pendingBatch);
                        pendingBatch.Clear();
                    }
                    store.SaveCheckpoint(new ScanCheckpoint(requestedStart, requestedEnd, upToOffset));
                    lastCheckpointOffset = upToOffset;
                }

                try
                {
                    if (dbPath is not null)
                    {
                        store = ScanStore.OpenOrCreate(dbPath);
                        store.SaveGeometry(new ScanGeometry(partitionOffset, bootSector.BytesPerSector, bootSector.BytesPerCluster, bootSector.MftRecordSize));

                        if (resumeRequested)
                        {
                            ScanCheckpoint? checkpoint = store.TryLoadCheckpoint();
                            if (checkpoint is { } cp && cp.ScanStartOffset == requestedStart && cp.ScanEndOffset == requestedEnd)
                            {
                                effectiveStart = cp.LastScannedOffset;
                                lastCheckpointOffset = effectiveStart;
                            }
                        }
                    }

                    var scanner = new MftScanner(device, bootSector.BytesPerSector, bootSector.MftRecordSize, blockSize);
                    ScanStatistics result = scanner.Scan(
                        effectiveStart,
                        requestedEnd - effectiveStart,
                        record =>
                        {
                            if (store is null)
                                return;
                            pendingBatch.Add(ScanRecordDto.FromMftRecord(record));
                            if (pendingBatch.Count >= BatchFlushSize)
                            {
                                store.InsertRecords(pendingBatch);
                                pendingBatch.Clear();
                            }
                        },
                        new SynchronousProgress<ScanProgress>(p =>
                        {
                            progress.Report(p);
                            long currentOffset = effectiveStart + p.BytesScanned;
                            if (store is not null && currentOffset - lastCheckpointOffset >= CheckpointIntervalBytes)
                                FlushAndCheckpoint(currentOffset);
                        }),
                        token);

                    FlushAndCheckpoint(requestedEnd);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    FlushAndCheckpoint(lastCheckpointOffset);
                    throw;
                }
                finally
                {
                    store?.Dispose();
                }
            }, token);

            // Sem um banco de dados persistente não há registros brutos sem o atributo
            // $DATA residente/não residente descartado durante a varredura -- nesse caso
            // a árvore não pode ser reconstruída (o motivo pelo qual --output passou a
            // ser fortemente recomendado). Com banco de dados, recarrega tudo do disco:
            // isso também garante correção ao retomar uma varredura (os registros de
            // antes do checkpoint já estão lá, não só os desta execução).
            List<ScanRecordDto> recordsForTree;
            if (dbPath is not null)
            {
                using ScanStore reloadStore = ScanStore.OpenOrCreate(dbPath);
                recordsForTree = reloadStore.LoadAllRecords();
            }
            else
            {
                MessageBox.Show(
                    "Nenhum caminho de banco de dados foi definido, então a árvore não pôde ser reconstruída a partir desta varredura. Defina um caminho de banco de dados e escaneie novamente.",
                    "Varredura sem persistência", MessageBoxButton.OK, MessageBoxImage.Warning);
                ScanStatusText = "Varredura concluída sem salvar -- nenhuma árvore para mostrar.";
                return;
            }

            List<ScanRecordDto> resolved = ScanRecordAttributeListResolver.Resolve(recordsForTree);
            var index = new VirtualMftIndex();
            foreach (ScanRecordDto dto in resolved)
                index.AddDto(dto);

            TreeBuildResult treeResult = NtfsTreeBuilder.Build(index);

            RootItems.Add(new TreeItemViewModel(treeResult.Root));
            SelectedTreeItem = RootItems[0];

            ScanStatusText =
                $"Candidatos: {stats.CandidatesFound}, " +
                $"Válidos: {stats.ValidRecords}, " +
                $"Arquivos: {stats.Files}, " +
                $"Diretórios: {stats.Directories}, " +
                $"Rejeitados: {stats.Rejected}, " +
                $"Orphans: {treeResult.OrphanCount}, " +
                $"Correspondências aproximadas: {treeResult.SoftMatchCount}";

            Log($"Varredura concluída: {stats.ValidRecords} registros válidos encontrados. Salvo em {dbPath}.");
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "Varredura cancelada (o progresso até o último checkpoint foi salvo).";
            Log("Varredura cancelada pelo usuário.");
        }
        catch (Exception ex)
        {
            ScanStatusText = "A varredura falhou.";
            Log($"ERRO durante a varredura: {ex.Message}");
            MessageBox.Show(ex.Message, "Falha na varredura", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            _scanCts = null;
        }
    }

    private void RefreshFileList()
    {
        FileListItems.Clear();
        if (SelectedTreeItem is null)
            return;

        foreach (RecoveryNode child in SelectedTreeItem.Node.Children.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            FileListItems.Add(new FileListItemViewModel(child));
    }

    private static string BuildMetadataPreview(RecoveryNode? node)
    {
        if (node is null)
            return "";

        if (node.Record is null)
            return $"{node.FullPath}\n(nó sintético; sem registro MFT)";

        VirtualMftRecord r = node.Record;
        return
            $"Caminho:         {node.FullPath}\n" +
            $"MFT Record:      {r.RecordNumber}\n" +
            $"Sequence:        {r.SequenceNumber}\n" +
            $"Estado:          {(r.IsInUse ? "in-use" : "deleted")}\n" +
            $"Criado:          {r.Dto.CreationTime?.ToString("u") ?? "(desconhecido)"}\n" +
            $"Modificado:      {r.Dto.ModificationTime?.ToString("u") ?? "(desconhecido)"}\n" +
            $"Tamanho lógico:  {r.Dto.LogicalSize:N0} bytes\n" +
            $"Non-resident:    {r.Dto.IsDataNonResident}\n" +
            $"Compactado:      {r.Dto.IsDataCompressed}\n" +
            $"Data runs:       {r.Dto.DataRuns.Count}";
    }

    private async Task RecoverSelectedAsync()
    {
        if (SelectedTreeItem is null || _device is null || _bootSector is null)
            return;

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            MessageBox.Show("Escolha uma pasta de destino primeiro.", "Nenhum destino", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(DestinationPath);

        SourceDiskGuard guard = SourceDiskGuard.Create(_sourceDescription);
        DestinationValidationResult safety = DestinationValidator.Validate(guard, DestinationPath);
        if (!safety.IsAllowed)
        {
            Log($"BLOQUEADO: {safety.ErrorMessage}");
            MessageBox.Show(safety.ErrorMessage, "Recuperação bloqueada", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        IsBusy = true;
        ScanStatusText = "Recuperando...";

        try
        {
            RecoveryNode root = SelectedTreeItem.Node;
            string destinationRoot = DestinationPath;
            int bytesPerCluster = _bootSector.BytesPerCluster;
            IBlockDevice device = _device;

            var (healthy, partial, unsupported, corrupt, metadataOnly) = await Task.Run(() =>
            {
                int h = 0, p = 0, u = 0, c = 0, m = 0;
                foreach (RecoveryNode fileNode in TreeNavigator.EnumerateFiles(root))
                {
                    string relative = GetRelativePath(root, fileNode);
                    string destPath = Path.Combine(destinationRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                    FileExtractionResult result = FileExtractor.Extract(device, bytesPerCluster, fileNode.Record!.Dto, destPath);
                    switch (result.Status)
                    {
                        case FileExtractionStatus.Healthy: h++; break;
                        case FileExtractionStatus.Partial: p++; break;
                        case FileExtractionStatus.Unsupported: u++; break;
                        case FileExtractionStatus.CorruptRuns: c++; break;
                        case FileExtractionStatus.MetadataOnly: m++; break;
                    }
                }
                return (h, p, u, c, m);
            });

            ScanStatusText = $"Recuperação concluída: Healthy {healthy}, Partial {partial}, Unsupported {unsupported}, CorruptRuns {corrupt}, MetadataOnly {metadataOnly}";
            Log(ScanStatusText);
        }
        catch (Exception ex)
        {
            Log($"ERRO durante a recuperação: {ex.Message}");
            MessageBox.Show(ex.Message, "Falha na recuperação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string GetRelativePath(RecoveryNode root, RecoveryNode node)
    {
        var segments = new List<string>();
        RecoveryNode? current = node;
        while (current is not null && current != root)
        {
            segments.Insert(0, current.Name);
            current = current.Parent;
        }
        if (segments.Count == 0)
            segments.Add(node.Name);
        return Path.Combine([.. segments]);
    }
}
