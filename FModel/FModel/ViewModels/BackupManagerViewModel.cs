using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels.ApiEndpoints.Models;
using FModel.Views.Resources.Controls;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using Serilog;

namespace FModel.ViewModels;

public class BackupManagerViewModel : ViewModel
{
    public const uint FBKP_MAGIC = 0x504B4246;

    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private readonly string _gameName;

    private Backup _selectedBackup;
    public Backup SelectedBackup
    {
        get => _selectedBackup;
        set => SetProperty(ref _selectedBackup, value);
    }

    public ObservableCollection<Backup> Backups { get; }
    public ICollectionView BackupsView { get; }

    public BackupManagerViewModel(string gameName)
    {
        _gameName = gameName;
        Backups = new ObservableCollection<Backup>();
        BackupsView = new ListCollectionView(Backups) { SortDescriptions = { new SortDescription("FileName", ListSortDirection.Ascending) } };
    }

    public async Task Initialize()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            var backups = _apiEndpointView.FModelApi.GetBackups(cancellationToken, _gameName);
            if (backups == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var backup in backups) Backups.Add(backup);
                SelectedBackup = Backups.LastOrDefault();
            });
        });
    }

    public async Task CreateBackup()
    {
        await _threadWorkerView.Begin(_ =>
        {
            var backupFolder = Path.Combine(UserSettings.Default.OutputDirectory, "Backups");
            var fileName = $"{_gameName}_{DateTime.Now:MM'_'dd'_'yyyy}.fbkp";
            var fullPath = Path.Combine(backupFolder, fileName);
            var func = new Func<GameFile, bool>(x => !x.IsUePackagePayload);

            using var fileStream = new FileStream(fullPath, FileMode.Create);
            using var compressedStream = LZ4Stream.Encode(fileStream, LZ4Level.L00_FAST);
            using var writer = new BinaryWriter(compressedStream);
            writer.Write(FBKP_MAGIC);
            writer.Write((byte) EBackupVersion.Latest);
            writer.Write(_applicationView.CUE4Parse.Provider.Files.Values.Count(func));

            foreach (var asset in _applicationView.CUE4Parse.Provider.Files.Values)
            {
                if (!func(asset)) continue;
                writer.Write(asset.Size);
                writer.Write(asset.IsEncrypted);
                writer.Write(asset.Path);
            }

            SaveCheck(fullPath, fileName, "created", "create");
        });
    }

    public async Task Download()
    {
        if (SelectedBackup == null) return;
        await _threadWorkerView.Begin(_ =>
        {
            var fullPath = Path.Combine(Path.Combine(UserSettings.Default.OutputDirectory, "Backups"), SelectedBackup.FileName);
            _apiEndpointView.DownloadFile(SelectedBackup.DownloadUrl, fullPath);
            SaveCheck(fullPath, SelectedBackup.FileName, "downloaded", "download");
        });
    }

    private void SaveCheck(string fullPath, string fileName, string type1, string type2)
    {
        if (new FileInfo(fullPath).Length > 0)
        {
            Log.Information("{FileName} successfully {Type}", fileName, type1);
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text($"Successfully {type1} ", Constants.WHITE);
                FLogger.Link(fileName, fullPath, true);
            });
        }
        else
        {
            Log.Error("{FileName} could not be {Type}", fileName, type1);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not {type2} '{fileName}'", Constants.WHITE, true));
        }
    }
}

public enum EBackupVersion : byte
{
    BeforeVersionWasAdded = 0,
    Initial,
    PerfectPath, // no more leading slash and ToLower

    LatestPlusOne,
    Latest = LatestPlusOne - 1
}
