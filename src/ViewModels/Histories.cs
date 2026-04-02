using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Histories : ObservableObject
    {
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public bool IsAuthorColumnVisible
        {
            get => _repo.UIStates.IsAuthorColumnVisibleInHistory;
            set
            {
                if (_repo.UIStates.IsAuthorColumnVisibleInHistory != value)
                {
                    _repo.UIStates.IsAuthorColumnVisibleInHistory = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSHAColumnVisible
        {
            get => _repo.UIStates.IsSHAColumnVisibleInHistory;
            set
            {
                if (_repo.UIStates.IsSHAColumnVisibleInHistory != value)
                {
                    _repo.UIStates.IsSHAColumnVisibleInHistory = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsAuthorTimeColumnVisible
        {
            get => _repo.UIStates.IsAuthorTimeColumnVisibleInHistory;
            set
            {
                if (_repo.UIStates.IsAuthorTimeColumnVisibleInHistory != value)
                {
                    _repo.UIStates.IsAuthorTimeColumnVisibleInHistory = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsCommitTimeColumnVisible
        {
            get => _repo.UIStates.IsCommitTimeColumnVisibleInHistory;
            set
            {
                if (_repo.UIStates.IsCommitTimeColumnVisibleInHistory != value)
                {
                    _repo.UIStates.IsCommitTimeColumnVisibleInHistory = value;
                    OnPropertyChanged();
                }
            }
        }

        public List<Models.Commit> Commits
        {
            get => _commits;
            set
            {
                var oldCommits = _commits;

                // Request scroll offset to be saved before the update
                if (oldCommits != null && oldCommits.Count > 0)
                    PendingScrollOffset = -1; // Signal to save current offset

                // Ignore selection changes while we're updating the commits list
                _ignoreSelectionChange = true;

                GenerateGraph(value, true);
                if (SetProperty(ref _commits, value))
                {
                    // Transfer build status from old commits to new commits
                    if (oldCommits != null && oldCommits.Count > 0 && value.Count > 0)
                    {
                        var oldBuildInfos = new Dictionary<string, List<Models.CommitBuildInfo>>();
                        foreach (var oldCommit in oldCommits)
                        {
                            if (oldCommit.BuildInfos != null)
                                oldBuildInfos[oldCommit.SHA] = oldCommit.BuildInfos;
                        }

                        if (oldBuildInfos.Count > 0)
                        {
                            foreach (var newCommit in value)
                            {
                                if (oldBuildInfos.TryGetValue(newCommit.SHA, out var buildInfos))
                                    newCommit.BuildInfos = buildInfos;
                            }
                        }
                    }

                    PostCommitsChanged();

                    // Start or update build server polling
                    if (value.Count > 0)
                    {
                        InitializeBuildServerPolling();
                        _buildServerPoller?.UpdateCommits(value);
                    }
                    else
                    {
                        _buildServerPoller?.StopPolling();
                    }
                }

                _ignoreSelectionChange = false;
            }
        }

        public Models.CommitGraph Graph
        {
            get => _graph;
            set => SetProperty(ref _graph, value);
        }

        public Models.CommitGraphHighlighting GraphHighlighting
        {
            get => _repo.UIStates.GraphHighlighting;
            set
            {
                if (_repo.UIStates.GraphHighlighting != value)
                {
                    _repo.UIStates.GraphHighlighting = value;
                    GenerateGraph(_commits);
                }
            }
        }

        public List<Models.Commit> SelectedCommits
        {
            get => _selectedCommits;
            set
            {
                var oldCount = _selectedCommits.Count;
                if (SetProperty(ref _selectedCommits, value) && oldCount + value.Count > 0)
                    PostSelectedCommitsChanged();
            }
        }

        public object DetailContext
        {
            get => _detailContext;
            set
            {
                if (SetProperty(ref _detailContext, value))
                    OnPropertyChanged(nameof(IsOpenAsStandaloneVisible));
            }
        }

        public Models.Bisect Bisect
        {
            get => _bisect;
            private set => SetProperty(ref _bisect, value);
        }

        public Models.Branch CurrentBranch
        {
            get => _repo.CurrentBranch;
        }

        public AvaloniaList<Models.IssueTracker> IssueTrackers
        {
            get => _repo.IssueTrackers;
        }

        public GridLength LeftArea
        {
            get => _leftArea;
            set => SetProperty(ref _leftArea, value);
        }

        public GridLength RightArea
        {
            get => _rightArea;
            set => SetProperty(ref _rightArea, value);
        }

        public GridLength TopArea
        {
            get => _topArea;
            set => SetProperty(ref _topArea, value);
        }

        public GridLength BottomArea
        {
            get => _isCollapseDetails ? new GridLength(28, GridUnitType.Pixel) : _bottomArea;
            set
            {
                if (!Preferences.Instance.UseTwoColumnsLayoutInHistories && !_isCollapseDetails)
                    SetProperty(ref _bottomArea, value);
            }
        }

        public double AuthorColumnWidth
        {
            get => _repo.UIStates.AuthorColumnWidth;
            set => _repo.UIStates.AuthorColumnWidth = value;
        }

        public bool IsOpenAsStandaloneVisible
        {
            get => DetailContext is CommitDetail or RevisionCompare;
        }

        public bool IsCollapseDetails
        {
            get => _isCollapseDetails;
            set
            {
                if (!Preferences.Instance.UseTwoColumnsLayoutInHistories && SetProperty(ref _isCollapseDetails, value))
                {
                    OnPropertyChanged(nameof(TopArea));
                    OnPropertyChanged(nameof(BottomArea));
                }
            }
        }

        public double? PendingScrollOffset
        {
            get => _pendingScrollOffset;
            set => SetProperty(ref _pendingScrollOffset, value);
        }

        public Histories(Repository repo)
        {
            _repo = repo;
            _commitDetailSharedData = new CommitDetailSharedData();
            _buildServerPoller = null;

            // Subscribe to repository property changes to detect when BuildServer config loads
            _repo.PropertyChanged += OnRepositoryPropertyChanged;
        }

        private void OnRepositoryPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Repository.BuildServer))
            {
                // BuildServer config has been loaded/changed, reinitialize polling
                ReinitializeBuildServerPolling();
            }
        }

        public void NotifyCurrentBranchChanged()
        {
            OnPropertyChanged(nameof(CurrentBranch));
        }

        public Models.BisectState UpdateBisectInfo()
        {
            var test = Path.Combine(_repo.GitDir, "BISECT_START");
            if (!File.Exists(test))
            {
                Bisect = null;
                return Models.BisectState.None;
            }

            var head = new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResult();
            var info = new Models.Bisect();
            var markedHead = false;
            var dir = Path.Combine(_repo.GitDir, "refs", "bisect");
            if (Directory.Exists(dir))
            {
                var files = new DirectoryInfo(dir).GetFiles();
                foreach (var file in files)
                {
                    var sha = File.ReadAllText(file.FullName).Trim();
                    if (!markedHead)
                        markedHead = head.Equals(sha, StringComparison.Ordinal);

                    if (file.Name.StartsWith("bad"))
                        info.Bads.Add(sha);
                    else if (file.Name.StartsWith("good"))
                        info.Goods.Add(sha);
                    else if (file.Name.StartsWith("skip"))
                        info.Skipped.Add(sha);
                }
            }

            Bisect = info;

            if (info.Bads.Count == 0)
                return Models.BisectState.WaitingForFirstBad;

            if (markedHead)
                return Models.BisectState.WaitingForCheckoutAnother;

            if (info.Goods.Count == 0)
                return Models.BisectState.WaitingForFirstGood;

            return Models.BisectState.WaitingForMark;
        }

        public void NavigateTo(string commitSHA)
        {
            var commit = _commits.Find(x => x.SHA.StartsWith(commitSHA, StringComparison.Ordinal));
            if (commit != null)
            {
                SelectedCommits = [commit];
                return;
            }

            Task.Run(async () =>
            {
                var c = await new Commands.QuerySingleCommit(_repo.FullPath, commitSHA)
                    .GetResultAsync()
                    .ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    _ignoreSelectionChange = true;
                    SelectedCommits = [];

                    if (_detailContext is CommitDetail detail)
                    {
                        detail.Commit = c;
                    }
                    else
                    {
                        var commitDetail = new CommitDetail(_repo, _commitDetailSharedData);
                        commitDetail.Commit = c;
                        DetailContext = commitDetail;
                    }

                    _ignoreSelectionChange = false;
                });
            });
        }

        public async Task<Models.Commit> GetCommitAsync(string sha)
        {
            return await new Commands.QuerySingleCommit(_repo.FullPath, sha)
                .GetResultAsync()
                .ConfigureAwait(false);
        }

        public void CheckoutCommitDetached(Models.Commit c)
        {
            if (!c.IsCurrentHead && _repo.CanCreatePopup())
                _repo.ShowPopup(new CheckoutDetached(_repo, c));
        }

        public async Task<bool> CheckoutBranchByDecoratorAsync(Models.Decorator decorator)
        {
            if (decorator == null)
                return false;

            if (decorator.Type == Models.DecoratorType.CurrentBranchHead ||
                decorator.Type == Models.DecoratorType.CurrentCommitHead)
                return true;

            if (decorator.Type == Models.DecoratorType.LocalBranchHead)
            {
                var b = _repo.Branches.Find(x => x.Name == decorator.Name);
                if (b == null)
                    return false;

                await _repo.CheckoutBranchAsync(b);
                return true;
            }

            if (decorator.Type == Models.DecoratorType.RemoteBranchHead)
            {
                var rb = _repo.Branches.Find(x => x.FriendlyName == decorator.Name);
                if (rb == null)
                    return false;

                var lb = _repo.Branches.Find(x => x.IsLocal && x.Upstream == rb.FullName);
                if (lb == null || lb.Ahead.Count > 0)
                {
                    if (_repo.CanCreatePopup())
                        _repo.ShowPopup(new CreateBranch(_repo, rb));
                }
                else if (lb.Behind.Count > 0)
                {
                    if (_repo.CanCreatePopup())
                        _repo.ShowPopup(new CheckoutAndFastForward(_repo, lb, rb));
                }
                else if (!lb.IsCurrent)
                {
                    await _repo.CheckoutBranchAsync(lb);
                }

                return true;
            }

            return false;
        }

        public async Task CheckoutBranchByCommitAsync(Models.Commit commit)
        {
            if (commit.IsCurrentHead)
                return;

            Models.Branch firstRemoteBranch = null;
            foreach (var d in commit.Decorators)
            {
                if (d.Type == Models.DecoratorType.LocalBranchHead)
                {
                    var b = _repo.Branches.Find(x => x.Name == d.Name);
                    if (b == null)
                        continue;

                    await _repo.CheckoutBranchAsync(b);
                    return;
                }

                if (d.Type == Models.DecoratorType.RemoteBranchHead)
                {
                    var rb = _repo.Branches.Find(x => x.FriendlyName == d.Name);
                    if (rb == null)
                        continue;

                    var lb = _repo.Branches.Find(x => x.IsLocal && x.Upstream == rb.FullName);
                    if (lb != null && lb.Behind.Count > 0 && lb.Ahead.Count == 0)
                    {
                        if (_repo.CanCreatePopup())
                            _repo.ShowPopup(new CheckoutAndFastForward(_repo, lb, rb));
                        return;
                    }

                    firstRemoteBranch ??= rb;
                }
            }

            if (_repo.CanCreatePopup())
            {
                if (firstRemoteBranch != null)
                    _repo.ShowPopup(new CreateBranch(_repo, firstRemoteBranch));
                else if (!_repo.IsBare)
                    _repo.ShowPopup(new CheckoutDetached(_repo, commit));
            }
        }

        public async Task CherryPickAsync(Models.Commit commit)
        {
            if (_repo.CanCreatePopup())
            {
                if (commit.Parents.Count <= 1)
                {
                    _repo.ShowPopup(new CherryPick(_repo, [commit]));
                }
                else
                {
                    var parents = new List<Models.Commit>();
                    foreach (var sha in commit.Parents)
                    {
                        var parent = _commits.Find(x => x.SHA.Equals(sha, StringComparison.Ordinal));
                        if (parent == null)
                            parent = await new Commands.QuerySingleCommit(_repo.FullPath, sha).GetResultAsync();

                        if (parent != null)
                            parents.Add(parent);
                    }

                    _repo.ShowPopup(new CherryPick(_repo, commit, parents));
                }
            }
        }

        public async Task<string> GetCommitFullMessageAsync(Models.Commit commit)
        {
            return await new Commands.QueryCommitFullMessage(_repo.FullPath, commit.SHA)
                .GetResultAsync()
                .ConfigureAwait(false);
        }

        public async Task<Models.Commit> CompareWithHeadAsync(Models.Commit commit)
        {
            var head = _commits.Find(x => x.IsCurrentHead);
            if (head == null)
            {
                _repo.SearchCommitContext.Selected = null;
                head = await new Commands.QuerySingleCommit(_repo.FullPath, "HEAD").GetResultAsync();
                if (head != null)
                    DetailContext = new RevisionCompare(_repo, commit, head);

                return null;
            }

            return head;
        }

        public void CompareWithWorktree(Models.Commit commit)
        {
            DetailContext = new RevisionCompare(_repo, commit, null);
        }

        private void PostCommitsChanged()
        {
            if (_selectedCommits.Count == 0)
                return;

            if (_commits.Count == 0 || _selectedCommits.Count > 20)
            {
                SelectedCommits = [];
                return;
            }

            var set = new HashSet<string>();
            foreach (var c in _selectedCommits)
                set.Add(c.SHA);

            var selected = new List<Models.Commit>();
            foreach (var c in _commits)
            {
                if (set.Contains(c.SHA))
                {
                    selected.Add(c);
                    set.Remove(c.SHA);
                    if (set.Count == 0)
                        break;
                }
            }

            SelectedCommits = selected;
        }

        private void PostSelectedCommitsChanged()
        {
            if (_ignoreSelectionChange)
                return;

            if (_selectedCommits.Count == 0)
            {
                _repo.SearchCommitContext.Selected = null;
                DetailContext = new Models.Null();
            }
            else if (_selectedCommits.Count == 1)
            {
                var c = _selectedCommits[0];
                if (_repo.SearchCommitContext.Selected == null || !_repo.SearchCommitContext.Selected.SHA.Equals(c.SHA, StringComparison.Ordinal))
                    _repo.SearchCommitContext.Selected = _repo.SearchCommitContext.Results?.Find(x => x.SHA.Equals(c.SHA, StringComparison.Ordinal));

                if (_detailContext is CommitDetail detail)
                    detail.Commit = c;
                else
                    DetailContext = new CommitDetail(_repo, _commitDetailSharedData) { Commit = c };
            }
            else if (_selectedCommits.Count == 2)
            {
                _repo.SearchCommitContext.Selected = null;

                if (_detailContext is RevisionCompare compare)
                    compare.SetTargets(_selectedCommits[1], _selectedCommits[0]);
                else
                    DetailContext = new RevisionCompare(_repo, _selectedCommits[1], _selectedCommits[0]);
            }
            else
            {
                _repo.SearchCommitContext.Selected = null;
                DetailContext = new Models.Count(_selectedCommits.Count);
            }

            if (_repo.UIStates.GraphHighlighting >= Models.CommitGraphHighlighting.SelectedCommitsOnly)
                GenerateGraph(_commits);
        }

        private void GenerateGraph(List<Models.Commit> commits, bool commitsChanged = false)
        {
            var firstParentOnly = _repo.UIStates.HistoryShowFlags.HasFlag(Models.HistoryShowFlags.FirstParentOnly);
            var highlighting = _repo.UIStates.GraphHighlighting;
            var extraHeads = new HashSet<string>();

            if (highlighting >= Models.CommitGraphHighlighting.SelectedCommitsOnly)
            {
                foreach (var c in _selectedCommits)
                    extraHeads.Add(c.SHA);
            }

            Graph = Models.CommitGraph.Generate(commits, commitsChanged, firstParentOnly, highlighting, extraHeads);
        }

        private Repository _repo = null;
        private CommitDetailSharedData _commitDetailSharedData = null;
        private BuildServerPoller _buildServerPoller = null;
        private bool _isLoading = true;
        private List<Models.Commit> _commits = [];
        private Models.CommitGraph _graph = null;
        private List<Models.Commit> _selectedCommits = [];
        private Models.Bisect _bisect = null;
        private object _detailContext = new Models.Null();
        private bool _ignoreSelectionChange = false;

        private GridLength _leftArea = new(1, GridUnitType.Star);
        private GridLength _rightArea = new(1, GridUnitType.Star);
        private GridLength _topArea = new(1, GridUnitType.Star);
        private GridLength _bottomArea = new(1, GridUnitType.Star);
        private bool _isCollapseDetails = false;
        private double? _pendingScrollOffset = null;

        private void ReinitializeBuildServerPolling()
        {
            // Stop and dispose the old poller
            _buildServerPoller?.Dispose();
            _buildServerPoller = null;

            // Now reinitialize if we have commits and a valid build server config
            if (_commits.Count > 0 && _repo?.BuildServer != null)
            {
                InitializeBuildServerPolling();
                _buildServerPoller?.StartPolling(_commits);
            }
        }

        private void InitializeBuildServerPolling()
        {
            if (_buildServerPoller != null)
                return;

            // Check if build server is configured before creating the poller
            var buildServer = _repo?.BuildServer;
            if (buildServer == null ||
                buildServer.Type != "Jenkins" ||
                !buildServer.EnableQueryBuildStatus ||
                string.IsNullOrEmpty(buildServer.ServerUrl) ||
                string.IsNullOrEmpty(buildServer.ProjectName))
            {
                return;
            }

            // Cache the adapter instance to avoid re-initialization on every poll
            Models.IBuildServerAdapter cachedAdapter = null;

            // Create adapter factory for Jenkins
            Models.IBuildServerAdapter CreateAdapter()
            {
                if (cachedAdapter != null)
                    return cachedAdapter;

                var currentBuildServer = _repo?.BuildServer;
                if (currentBuildServer == null)
                    return null;

                // Check if build server is enabled and properly configured
                if (currentBuildServer.Type != "Jenkins" ||
                    !currentBuildServer.EnableQueryBuildStatus ||
                    string.IsNullOrEmpty(currentBuildServer.ServerUrl) ||
                    string.IsNullOrEmpty(currentBuildServer.ProjectName))
                    return null;

                var adapter = new Models.JenkinsBuildServerAdapter();
                adapter.Initialize(currentBuildServer, _repo.FullPath);
                cachedAdapter = adapter;
                return adapter;
            }

            _buildServerPoller = new BuildServerPoller(
                _repo.FullPath,
                CreateAdapter,
                () =>
                {
                    // Force UI refresh after build statuses have been updated
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_commits != null && _commits.Count > 0)
                        {
                            // Remember the currently selected commit
                            var lastSelected = _selectedCommit;

                            // Request scroll offset to be saved before the update
                            PendingScrollOffset = -1;

                            // Ignore selection changes while updating the commits list
                            _ignoreSelectionChange = true;

                            // Create a new list to force DataGrid to rebind and re-render all rows
                            var updatedCommits = new List<Models.Commit>(_commits);
                            SetProperty(ref _commits, updatedCommits, nameof(Commits));

                            // Restore selection if there was one
                            if (lastSelected != null)
                            {
                                var foundCommit = updatedCommits.Find(x => x.SHA == lastSelected.SHA);
                                if (foundCommit != null)
                                {
                                    _selectedCommit = foundCommit;
                                    OnPropertyChanged(nameof(SelectedCommit));

                                    // Update detail context with the found commit
                                    if (_detailContext is CommitDetail detail)
                                        detail.Commit = foundCommit;
                                }
                            }

                            _ignoreSelectionChange = false;
                        }
                    });
                });

            if (_commits.Count > 0)
                _buildServerPoller.StartPolling(_commits);
        }
    }
}
