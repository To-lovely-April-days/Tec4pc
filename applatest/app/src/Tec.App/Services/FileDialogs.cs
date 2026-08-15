using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Tec.Core.Persistence;

namespace Tec.App.Services;

/// <summary>
/// 打开 / 保存对话框。ViewModel 不认识窗口，所以窗口从这里取——
/// 唯一的一处 UI 依赖，换宿主时只改这一个文件。
/// </summary>
public static class FileDialogs
{
    private static readonly FilePickerFileType Experiment =
        new("TecStudio 实验") { Patterns = new[] { "*" + TecFiles.ExperimentExt } };

    private static readonly FilePickerFileType BenchFile =
        new("TecStudio 台面") { Patterns = new[] { "*" + TecFiles.BenchExt } };

    private static readonly FilePickerFileType RecipeFile =
        new("TecStudio 配方") { Patterns = new[] { "*" + TecFiles.RecipeExt } };

    private static Window? MainWindow
        => (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static async Task<IStorageFolder?> StartFolder(string path)
    {
        var top = MainWindow;
        if (top is null) return null;
        try
        {
            Directory.CreateDirectory(path);
            return await top.StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch { return null; }
    }

    public static Task<string?> OpenExperiment() => Open("打开实验", Experiment, ExperimentStore.ExperimentsDir);
    public static Task<string?> OpenBench() => Open("导入台面", BenchFile, ExperimentStore.DataDir);
    public static Task<string?> OpenRecipe() => Open("导入配方", RecipeFile, ExperimentStore.DataDir);

    public static Task<string?> SaveExperiment(string suggested)
        => Save("保存实验", Experiment, TecFiles.ExperimentExt, suggested, ExperimentStore.ExperimentsDir);

    public static Task<string?> SaveBench(string suggested)
        => Save("导出台面", BenchFile, TecFiles.BenchExt, suggested, ExperimentStore.DataDir);

    public static Task<string?> SaveRecipe(string suggested)
        => Save("导出配方", RecipeFile, TecFiles.RecipeExt, suggested, ExperimentStore.DataDir);

    private static async Task<string?> Open(string title, FilePickerFileType type, string startIn)
    {
        var top = MainWindow;
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { type, FilePickerFileTypes.All },
            SuggestedStartLocation = await StartFolder(startIn)
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static async Task<string?> Save(string title, FilePickerFileType type, string ext,
                                            string suggested, string startIn)
    {
        var top = MainWindow;
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = Sanitize(suggested) + ext,
            DefaultExtension = ext.TrimStart('.'),
            FileTypeChoices = new[] { type },
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await StartFolder(startIn)
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>实验名可以随便起，文件名不行——非法字符换成下划线。</summary>
    public static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "未命名实验" : s;
    }
}
