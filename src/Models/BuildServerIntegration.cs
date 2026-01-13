using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Models
{
    public class BuildServerIntegration : ObservableObject
    {
        public bool IsShared
        {
            get => _isShared;
            set => SetProperty(ref _isShared, value);
        }

        public string Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
        }

        public string ServerUrl
        {
            get => _serverUrl;
            set => SetProperty(ref _serverUrl, value);
        }

        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value);
        }

        public bool EnableQueryBuildStatus
        {
            get => _enableQueryBuildStatus;
            set => SetProperty(ref _enableQueryBuildStatus, value);
        }

        private bool _isShared;
        private string _type = "Jenkins";
        private string _serverUrl = string.Empty;
        private string _projectName = string.Empty;
        private bool _enableQueryBuildStatus = false;
    }
}
