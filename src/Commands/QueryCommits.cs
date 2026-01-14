using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCommits : Command
    {
        public QueryCommits(string repo, string limits, bool markMerged = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            // Use %x1e (record separator) between commits, %x00 between fields, and %x1f before body (which may contain newlines)
            Args = $"log --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s%x1f%b%x1e {limits}";
            _markMerged = markMerged;
        }

        public QueryCommits(string repo, string filter, Models.CommitSearchMethod method, bool onlyCurrentBranch)
        {
            var builder = new StringBuilder();
            // Use %x1e (record separator) between commits, %x00 between fields, and %x1f before body (which may contain newlines)
            builder.Append("log -1000 --date-order --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s%x1f%b%x1e ");

            if (!onlyCurrentBranch)
                builder.Append("--branches --remotes ");

            if (method == Models.CommitSearchMethod.ByAuthor)
            {
                builder.Append("-i --author=").Append(filter.Quoted());
            }
            else if (method == Models.CommitSearchMethod.ByMessage)
            {
                var words = filter.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                    builder.Append("--grep=").Append(word.Trim().Quoted()).Append(' ');
                builder.Append("--all-match -i");
            }
            else if (method == Models.CommitSearchMethod.ByPath)
            {
                builder.Append("-- ").Append(filter.Quoted());
            }
            else
            {
                builder.Append("-G").Append(filter.Quoted());
            }

            WorkingDirectory = repo;
            Context = repo;
            Args = builder.ToString();
            _markMerged = false;
        }

        public async Task<List<Models.Commit>> GetResultAsync()
        {
            var commits = new List<Models.Commit>();
            try
            {
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                // Read entire output and split by record separator (0x1e)
                var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);

                var findHead = false;
                var records = output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries);

                foreach (var record in records)
                {
                    var trimmedRecord = record.Trim();
                    if (string.IsNullOrEmpty(trimmedRecord))
                        continue;

                    var parts = trimmedRecord.Split('\0');
                    if (parts.Length != 8)
                        continue;

                    // The 8th part contains subject + unit separator + body
                    var subjectAndBody = parts[7].Split('\x1f', 2);
                    var subject = subjectAndBody[0];
                    var body = subjectAndBody.Length > 1 ? ExtractFirstLineOfBody(subjectAndBody[1]) : string.Empty;

                    var commit = new Models.Commit() { SHA = parts[0] };
                    commit.ParseParents(parts[1]);
                    commit.ParseDecorators(parts[2]);
                    commit.Author = Models.User.FindOrAdd(parts[3]);
                    commit.AuthorTime = ulong.Parse(parts[4]);
                    commit.Committer = Models.User.FindOrAdd(parts[5]);
                    commit.CommitterTime = ulong.Parse(parts[6]);
                    commit.Subject = subject;
                    commit.Body = body;
                    commits.Add(commit);

                    if (!findHead && commit.IsMerged)
                        findHead = true;
                }

                if (_markMerged && !findHead && commits.Count > 0)
                {
                    var set = await new QueryCurrentBranchCommitHashes(WorkingDirectory, commits[^1].CommitterTime)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    foreach (var c in commits)
                    {
                        if (set.Contains(c.SHA))
                        {
                            c.IsMerged = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                RaiseException($"Failed to query commits. Reason: {e.Message}");
            }

            return commits;
        }

        private static string ExtractFirstLineOfBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;

            // Find the first non-empty line in the body
            var lines = body.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    return trimmed;
            }

            return string.Empty;
        }

        private bool _markMerged = false;
    }
}
