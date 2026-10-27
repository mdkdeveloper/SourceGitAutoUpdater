using CommunityToolkit.Mvvm.ComponentModel;
using SourceGitAutoUpdater.Core;

namespace SourceGitAutoUpdater.ViewModels;

public sealed class UpdateViewModel : ObservableObject
{
    private ReleaseInfo? _release;
    public ReleaseInfo? Release => _release;
    public bool HasRelease => _release is not null;
    public bool IsFirstInstall { get; }
    public bool ShowChoices => !IsFirstInstall;
    public bool ShowCancel => IsFirstInstall;
    public string CurrentVersion { get; }
    public string TagName => _release?.TagName ?? "";
    public string ReleaseDate => _release?.PublishedAt.ToLocalTime().ToString("g") ?? "";
    public string Body => _release?.Body ?? "";
    public string UpdateLabel => Texts.Update;
    public string SetupLabel => Texts.T("Installing SourceGit", "Встановлення SourceGit");
    public string CancelLabel => Texts.T("Cancel", "Скасувати");
    public string Attribution => "SourceGit by sourcegit-scm and contributors";
    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanChoose)); OnPropertyChanged(nameof(CanClose)); } }
    }
    private bool _canCancel;
    public bool CanCancel
    {
        get => _canCancel;
        set { if (SetProperty(ref _canCancel, value)) OnPropertyChanged(nameof(CanClose)); }
    }
    public bool CanChoose => !IsBusy && !IsFirstInstall;
    public bool CanClose => IsFirstInstall ? CanCancel : CanChoose;
    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    private double _progress;
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    private bool _indeterminate = true;
    public bool IsIndeterminate { get => _indeterminate; set => SetProperty(ref _indeterminate, value); }

    public UpdateViewModel()
    {
        IsFirstInstall = true;
        IsBusy = true;
        CanCancel = true;
        CurrentVersion = Texts.T("Not installed", "Не встановлено");
        Status = Texts.T("Checking the latest stable SourceGit release…", "Перевірка останнього стабільного релізу SourceGit…");
    }

    public UpdateViewModel(ReleaseInfo release, Version? current)
    {
        _release = release;
        CurrentVersion = current is null ? Texts.T("Not installed", "Не встановлено") : $"v{current.Major}.{current.Minor:D2}";
    }

    public void SetRelease(ReleaseInfo release)
    {
        _release = release;
        foreach (var name in new[] { nameof(Release), nameof(HasRelease), nameof(TagName), nameof(ReleaseDate), nameof(Body) })
            OnPropertyChanged(name);
    }
}
