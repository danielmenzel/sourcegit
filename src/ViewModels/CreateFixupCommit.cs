using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CreateFixupCommit : Popup
    {
        public Models.Commit Target
        {
            get;
        }

        public bool IsSquash
        {
            get => _isSquash;
            set => SetProperty(ref _isSquash, value);
        }

        public bool AutoStage
        {
            get => _autoStage;
            set => SetProperty(ref _autoStage, value);
        }

        public CreateFixupCommit(Repository repo, Models.Commit target)
        {
            _repo = repo;
            Target = target;
            IsSquash = false;
            AutoStage = false;
        }

        public override async Task<bool> Sure()
        {
            if (_repo.LocalChangesCount == 0 && !AutoStage)
            {
                App.RaiseException(_repo.FullPath, "No changes to commit!");
                return false;
            }

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = IsSquash ? "Creating squash commit..." : "Creating fixup commit...";

            var log = _repo.CreateLog(IsSquash ? "Create Squash Commit" : "Create Fixup Commit");
            Use(log);

            var prefix = IsSquash ? "squash!" : "fixup!";
            var message = $"{prefix} {Target.Subject}";

            if (AutoStage)
            {
                var succ = await new Commands.Add(_repo.FullPath, true)
                    .Use(log)
                    .ExecAsync();

                if (!succ)
                {
                    log.Complete();
                    return false;
                }
            }

            var signOff = _repo.Settings.EnableSignOffForCommit;
            var noVerify = _repo.Settings.NoVerifyOnCommit;
            
            var commitSucc = await new Commands.Commit(_repo.FullPath, message, signOff, noVerify, false, false)
                .Use(log)
                .RunAsync();

            log.Complete();
            return commitSucc;
        }

        private readonly Repository _repo = null;
        private bool _isSquash = false;
        private bool _autoStage = false;
    }
}
