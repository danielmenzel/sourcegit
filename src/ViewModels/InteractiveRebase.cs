using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public record InteractiveRebasePrefill(string SHA, Models.InteractiveRebaseAction Action);

    public class InteractiveRebaseItem : ObservableObject
    {
        public int OriginalOrder
        {
            get;
        }

        public Models.Commit Commit
        {
            get;
        }

        public Models.InteractiveRebaseAction Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }

        public Models.InteractiveRebasePendingType PendingType
        {
            get => _pendingType;
            set => SetProperty(ref _pendingType, value);
        }

        public string Subject
        {
            get => _subject;
            private set => SetProperty(ref _subject, value);
        }

        public string FullMessage
        {
            get => _fullMessage;
            set
            {
                if (SetProperty(ref _fullMessage, value))
                {
                    var normalized = value.ReplaceLineEndings("\n");
                    var parts = normalized.Split("\n\n", 2);
                    Subject = parts[0].ReplaceLineEndings(" ");
                }
            }
        }

        public string OriginalFullMessage
        {
            get;
            set;
        }

        public bool CanSquashOrFixup
        {
            get => _canSquashOrFixup;
            set => SetProperty(ref _canSquashOrFixup, value);
        }

        public bool ShowEditMessageButton
        {
            get => _showEditMessageButton;
            set => SetProperty(ref _showEditMessageButton, value);
        }

        public bool IsFullMessageUsed
        {
            get => _isFullMessageUsed;
            set => SetProperty(ref _isFullMessageUsed, value);
        }

        public Thickness DropDirectionIndicator
        {
            get => _dropDirectionIndicator;
            set => SetProperty(ref _dropDirectionIndicator, value);
        }

        public bool IsMessageUserEdited
        {
            get;
            set;
        } = false;

        public InteractiveRebaseItem(int order, Models.Commit c, string message)
        {
            OriginalOrder = order;
            Commit = c;
            FullMessage = message;
            OriginalFullMessage = message;
        }

        private Models.InteractiveRebaseAction _action = Models.InteractiveRebaseAction.Pick;
        private Models.InteractiveRebasePendingType _pendingType = Models.InteractiveRebasePendingType.None;
        private string _subject;
        private string _fullMessage;
        private bool _canSquashOrFixup = true;
        private bool _showEditMessageButton = false;
        private bool _isFullMessageUsed = true;
        private Thickness _dropDirectionIndicator = new Thickness(0);
    }

    public class InteractiveRebase : ObservableObject
    {
        public Models.Branch Current
        {
            get;
            private set;
        }

        public Models.Commit On
        {
            get;
        }

        public bool AutoStash
        {
            get;
            set;
        } = true;

        public bool AutoDetectFixupCommits
        {
            get => _autoDetectFixupCommits;
            set
            {
                if (SetProperty(ref _autoDetectFixupCommits, value))
                {
                    if (value)
                        ApplyAutoDetectFixupCommits();
                    else
                        ResetAutoDetectedActions();
                }
            }
        }

        public AvaloniaList<Models.IssueTracker> IssueTrackers
        {
            get => _repo.IssueTrackers;
        }

        public string ConventionalTypesOverride
        {
            get => _repo.Settings.ConventionalTypesOverride;
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public AvaloniaList<InteractiveRebaseItem> Items
        {
            get;
        } = [];

        public InteractiveRebaseItem PreSelected
        {
            get => _preSelected;
            private set => SetProperty(ref _preSelected, value);
        }

        public object Detail
        {
            get => _detail;
            private set => SetProperty(ref _detail, value);
        }

        public InteractiveRebase(Repository repo, Models.Commit on, InteractiveRebasePrefill prefill = null)
        {
            _repo = repo;
            _commitDetail = new CommitDetail(repo, null);
            Current = repo.CurrentBranch;
            On = on;
            IsLoading = true;

            Task.Run(async () =>
            {
                var commits = await new Commands.QueryCommitsForInteractiveRebase(_repo.FullPath, on.SHA)
                    .GetResultAsync()
                    .ConfigureAwait(false);

                var list = new List<InteractiveRebaseItem>();
                for (var i = 0; i < commits.Count; i++)
                {
                    var c = commits[i];
                    list.Add(new InteractiveRebaseItem(commits.Count - i, c.Commit, c.Message));
                }

                var selected = list.Count > 0 ? list[0] : null;
                if (prefill != null)
                {
                    var item = list.Find(x => x.Commit.SHA.Equals(prefill.SHA, StringComparison.Ordinal));
                    if (item != null)
                    {
                        item.Action = prefill.Action;
                        selected = item;
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    Items.AddRange(list);
                    AutoDetectFixupCommits = true;
                    UpdateItems();
                    PreSelected = selected;
                    IsLoading = false;
                });
            });
        }

        public void SelectCommits(List<InteractiveRebaseItem> items)
        {
            if (items.Count == 0)
            {
                Detail = null;
            }
            else if (items.Count == 1)
            {
                _commitDetail.Commit = items[0].Commit;
                Detail = _commitDetail;
            }
            else
            {
                Detail = new Models.Count(items.Count);
            }
        }

        public void ChangeAction(List<InteractiveRebaseItem> selected, Models.InteractiveRebaseAction action)
        {
            if (action == Models.InteractiveRebaseAction.Squash || action == Models.InteractiveRebaseAction.Fixup)
            {
                foreach (var item in selected)
                {
                    if (item.CanSquashOrFixup)
                    {
                        item.Action = action;
                        _manuallyModifiedItems.Add(item);
                    }
                }
            }
            else
            {
                foreach (var item in selected)
                {
                    item.Action = action;
                    _manuallyModifiedItems.Add(item);
                }
            }

            UpdateItems();
        }

        public void Move(List<InteractiveRebaseItem> commits, int index)
        {
            var hashes = new HashSet<string>();
            foreach (var c in commits)
                hashes.Add(c.Commit.SHA);

            var before = new List<InteractiveRebaseItem>();
            var ordered = new List<InteractiveRebaseItem>();
            var after = new List<InteractiveRebaseItem>();

            for (int i = 0; i < index; i++)
            {
                var item = Items[i];
                if (!hashes.Contains(item.Commit.SHA))
                    before.Add(item);
                else
                    ordered.Add(item);
            }

            for (int i = index; i < Items.Count; i++)
            {
                var item = Items[i];
                if (!hashes.Contains(item.Commit.SHA))
                    after.Add(item);
                else
                    ordered.Add(item);
            }

            Items.Clear();
            Items.AddRange(before);
            Items.AddRange(ordered);
            Items.AddRange(after);
            UpdateItems();
        }

        public async Task<bool> Start()
        {
            using var lockWatcher = _repo.LockWatcher();

            var saveFile = Path.Combine(_repo.GitDir, "sourcegit.interactive_rebase");
            var collection = new Models.InteractiveRebaseJobCollection();
            collection.OrigHead = _repo.CurrentBranch.Head;
            collection.Onto = On.SHA;

            InteractiveRebaseItem pending = null;
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var item = Items[i];
                var job = new Models.InteractiveRebaseJob()
                {
                    SHA = item.Commit.SHA,
                    Action = item.Action,
                };

                if (pending != null && item.PendingType != Models.InteractiveRebasePendingType.Ignore)
                    job.Message = pending.FullMessage;
                else
                    job.Message = item.FullMessage;

                collection.Jobs.Add(job);

                if (item.PendingType == Models.InteractiveRebasePendingType.Last)
                    pending = null;
                else if (item.PendingType == Models.InteractiveRebasePendingType.Target)
                    pending = item;
            }

            await using (var stream = File.Create(saveFile))
            {
                await JsonSerializer.SerializeAsync(stream, collection, JsonCodeGen.Default.InteractiveRebaseJobCollection);
            }

            var log = _repo.CreateLog("Interactive Rebase");
            var succ = await new Commands.InteractiveRebase(_repo.FullPath, On.SHA, AutoStash)
                .Use(log)
                .ExecAsync();

            log.Complete();
            return succ;
        }

        private void ApplyAutoDetectFixupCommits()
        {
            var fixupsToReorder = new List<(InteractiveRebaseItem fixup, string targetSubject, bool isSquash)>();

            // First pass: identify fixup/squash commits
            foreach (var item in Items)
            {
                var subject = item.Commit.Subject;

                if (subject.StartsWith("fixup! ", StringComparison.Ordinal))
                {
                    var targetSubject = subject.Substring(7); // Remove "fixup! " prefix
                    fixupsToReorder.Add((item, targetSubject, false));
                }
                else if (subject.StartsWith("squash! ", StringComparison.Ordinal))
                {
                    var targetSubject = subject.Substring(8); // Remove "squash! " prefix
                    fixupsToReorder.Add((item, targetSubject, true));
                }
            }

            // Second pass: reorder fixup/squash commits to be right before their targets
            // Note: The list is ordered from newest (top, index 0) -> oldest (bottom, index N)
            // In interactive rebase, fixup commits are applied AFTER their target chronologically,
            // meaning they are NEWER, so they should appear at a LOWER index (before the target in the list)
            if (fixupsToReorder.Count > 0)
            {
                var reordered = new List<InteractiveRebaseItem>(Items);

                // Process fixups in order (from top to bottom) so that moving items
                // doesn't affect the indices of fixups we haven't processed yet
                for (int f = 0; f < fixupsToReorder.Count; f++)
                {
                    var (fixupItem, targetSubject, isSquash) = fixupsToReorder[f];

                    // Find the target commit by matching the subject
                    // Search from the beginning (newest to oldest) to find the first non-fixup commit with this subject
                    int targetIndex = -1;

                    for (int i = 0; i < reordered.Count; i++)
                    {
                        var item = reordered[i];
                        if (item == fixupItem)
                            continue;

                        // Check if this commit's subject matches the target
                        if (item.Commit.Subject.Equals(targetSubject, StringComparison.Ordinal))
                        {
                            targetIndex = i;
                            break; // Use the first match (the target commit)
                        }
                    }

                    // If we found the target, move the fixup commit right before it (at the same index)
                    if (targetIndex >= 0)
                    {
                        var currentIndex = reordered.IndexOf(fixupItem);
                        if (currentIndex >= 0 && currentIndex != targetIndex)
                        {
                            // Remove the fixup commit from its current position
                            reordered.RemoveAt(currentIndex);

                            // Recalculate target index if removal affected it
                            if (currentIndex < targetIndex)
                                targetIndex--;

                            // Insert right before the target (fixup will be at targetIndex, target moves to targetIndex+1)
                            reordered.Insert(targetIndex, fixupItem);
                        }
                    }
                }

                // Update the Items collection with the reordered list
                Items.Clear();
                Items.AddRange(reordered);
            }

            // Now set the actions after reordering is complete
            foreach (var item in Items)
            {
                var subject = item.Commit.Subject;

                if (subject.StartsWith("fixup! ", StringComparison.Ordinal))
                {
                    if (!_manuallyModifiedItems.Contains(item))
                        item.Action = Models.InteractiveRebaseAction.Fixup;
                }
                else if (subject.StartsWith("squash! ", StringComparison.Ordinal))
                {
                    if (!_manuallyModifiedItems.Contains(item))
                        item.Action = Models.InteractiveRebaseAction.Squash;
                }
            }

            UpdateItems();
        }

        private void ResetAutoDetectedActions()
        {
            // First, restore original order
            var originalOrder = new List<InteractiveRebaseItem>(Items);
            originalOrder.Sort((a, b) => b.OriginalOrder.CompareTo(a.OriginalOrder));
            
            Items.Clear();
            Items.AddRange(originalOrder);

            // Then reset actions
            foreach (var item in Items)
            {
                var subject = item.Commit.Subject;

                if ((subject.StartsWith("fixup! ", StringComparison.Ordinal) ||
                     subject.StartsWith("squash! ", StringComparison.Ordinal)) &&
                    !_manuallyModifiedItems.Contains(item))
                {
                    item.Action = Models.InteractiveRebaseAction.Pick;
                }
            }

            UpdateItems();
        }

        private void UpdateItems()
        {
            if (Items.Count == 0)
                return;

            // Determine which items can be Squash/Fixup.
            // List order is newest (top, index 0) -> oldest (bottom). A commit can squash/fixup
            // only if there is already a non-drop commit before it (a parent to squash/fixup into).
            var hasPreviousNonDrop = false;
            for (var i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                if (hasPreviousNonDrop)
                {
                    item.CanSquashOrFixup = true;
                }
                else
                {
                    item.CanSquashOrFixup = false;
                    // If this item was marked Squash/Fixup but there's no previous non-drop,
                    // reset it to Pick to keep a valid sequence.
                    if (item.Action == Models.InteractiveRebaseAction.Squash || item.Action == Models.InteractiveRebaseAction.Fixup)
                        item.Action = Models.InteractiveRebaseAction.Pick;
                }

                if (item.Action != Models.InteractiveRebaseAction.Drop)
                    hasPreviousNonDrop = true;
            }

            var hasPending = false;
            var pendingMessages = new List<string>();
            for (var i = 0; i < Items.Count; i++)
            {
                var item = Items[i];

                if (item.Action == Models.InteractiveRebaseAction.Drop)
                {
                    item.IsFullMessageUsed = false;
                    item.ShowEditMessageButton = false;
                    item.PendingType = hasPending ? Models.InteractiveRebasePendingType.Ignore : Models.InteractiveRebasePendingType.None;
                    item.FullMessage = item.OriginalFullMessage;
                    item.IsMessageUserEdited = false;
                    continue;
                }

                if (item.Action == Models.InteractiveRebaseAction.Fixup ||
                    item.Action == Models.InteractiveRebaseAction.Squash)
                {
                    item.IsFullMessageUsed = false;
                    item.ShowEditMessageButton = false;
                    item.PendingType = hasPending ? Models.InteractiveRebasePendingType.Pending : Models.InteractiveRebasePendingType.Last;
                    item.FullMessage = item.OriginalFullMessage;
                    item.IsMessageUserEdited = false;

                    if (item.Action == Models.InteractiveRebaseAction.Squash)
                        pendingMessages.Add(item.OriginalFullMessage);

                    hasPending = true;
                    continue;
                }

                if (item.Action == Models.InteractiveRebaseAction.Reword ||
                    item.Action == Models.InteractiveRebaseAction.Edit)
                {
                    var oldPendingType = item.PendingType;
                    item.IsFullMessageUsed = true;
                    item.ShowEditMessageButton = true;
                    item.PendingType = hasPending ? Models.InteractiveRebasePendingType.Target : Models.InteractiveRebasePendingType.None;

                    if (hasPending)
                    {
                        if (!item.IsMessageUserEdited)
                        {
                            var builder = new StringBuilder();
                            builder.Append(item.OriginalFullMessage);
                            for (var j = pendingMessages.Count - 1; j >= 0; j--)
                                builder.Append("\n").Append(pendingMessages[j]);

                            item.FullMessage = builder.ToString();
                        }

                        hasPending = false;
                        pendingMessages.Clear();
                    }
                    else if (oldPendingType == Models.InteractiveRebasePendingType.Target)
                    {
                        if (!item.IsMessageUserEdited)
                            item.FullMessage = item.OriginalFullMessage;
                    }

                    continue;
                }

                if (item.Action == Models.InteractiveRebaseAction.Pick)
                {
                    item.IsFullMessageUsed = true;
                    item.IsMessageUserEdited = false;

                    if (hasPending)
                    {
                        var builder = new StringBuilder();
                        builder.Append(item.OriginalFullMessage);
                        for (var j = pendingMessages.Count - 1; j >= 0; j--)
                            builder.Append("\n").Append(pendingMessages[j]);

                        item.Action = Models.InteractiveRebaseAction.Reword;
                        item.PendingType = Models.InteractiveRebasePendingType.Target;
                        item.ShowEditMessageButton = true;
                        item.FullMessage = builder.ToString();

                    	hasPending = false;
                        pendingMessages.Clear();
                    }
                    else
                    {
                        item.PendingType = Models.InteractiveRebasePendingType.None;
                        item.ShowEditMessageButton = false;
                        item.FullMessage = item.OriginalFullMessage;
                    }
                }
            }
        }

        private Repository _repo = null;
        private bool _isLoading = false;
        private InteractiveRebaseItem _preSelected = null;
        private object _detail = null;
        private CommitDetail _commitDetail = null;
        private bool _autoDetectFixupCommits = false;
        private HashSet<InteractiveRebaseItem> _manuallyModifiedItems = new();
    }
}
