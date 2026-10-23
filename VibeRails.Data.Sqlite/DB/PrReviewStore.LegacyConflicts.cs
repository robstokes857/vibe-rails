using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VibeRails.DB;

public sealed partial class PrReviewStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<PrReviewLegacyConflict>> GetLegacyConflictsAsync(string repository, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        using var tx = db.BeginTransaction(deferred: true);
        var result = new List<PrReviewLegacyConflict>();
        var conflicts = ReadRows(db, tx, """
            SELECT a.Number, a.ConflictSourceHash,
                (SELECT SourceSnapshot FROM PrReviewLegacyConflicts c
                 WHERE c.Repository=a.Repository AND c.Number=a.Number AND c.SourceHash=a.ConflictSourceHash LIMIT 1)
            FROM PrReviewLegacyAdoptions a WHERE a.Repository=$repo AND a.ConflictSourceHash IS NOT NULL;
            """, ("$repo", repository));
        foreach (var row in conflicts)
        {
            var number = checked((int)(long)row[0]);
            var current = ReadSnapshot(db, tx, repository, number);
            result.Add(new(number, (string)row[1], Fingerprint(current, contentOnly: true),
                MapHistory(DeserializeSnapshot((byte[])row[2])), MapHistory(current)) { Repository = repository });
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> ResolveLegacyConflictAsync(string repository, int number, string sourceHash, string targetHash,
        bool useLegacy, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        using var tx = db.BeginTransaction(deferred: false);
        var current = ReadSnapshot(db, tx, repository, number);
        if (Fingerprint(current, contentOnly: true) != targetHash) return false;
        using var probe = Command(db, tx, """
            SELECT c.SourceSnapshot FROM PrReviewLegacyAdoptions a JOIN PrReviewLegacyConflicts c
              ON c.Repository=a.Repository AND c.Number=a.Number AND c.SourceHash=a.ConflictSourceHash
            WHERE a.Repository=$repo AND a.Number=$number AND a.ConflictSourceHash=$source LIMIT 1;
            """, ("$repo", repository), ("$number", number), ("$source", sourceHash));
        if (probe.ExecuteScalar() is not byte[] bytes) return false;
        var legacy = DeserializeSnapshot(bytes);
        ArchiveConflict(db, tx, repository, number, sourceHash, targetHash, legacy, current);
        if (useLegacy) CopySnapshot(db, tx, repository, number, WithLatestLifecycle(legacy, current));
        RecordAdoption(db, tx, repository, number, legacy);
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return true;
    }

    private static void RecordAdoption(SqliteConnection db, SqliteTransaction tx, string repository, int number, LegacySnapshot source) =>
        Execute(db, tx, """
            INSERT INTO PrReviewLegacyAdoptions
                (Repository,Number,SourceHash,TargetHash,SourceContentHash,TargetContentHash,ConflictSourceHash)
            VALUES ($repo,$number,$source,$target,$sourceContent,$targetContent,NULL)
            ON CONFLICT (Repository,Number) DO UPDATE SET SourceHash=$source,TargetHash=$target,
                SourceContentHash=$sourceContent,TargetContentHash=$targetContent,ConflictSourceHash=NULL;
            """, ("$repo", repository), ("$number", number), ("$source", Fingerprint(source)),
            ("$target", Fingerprint(ReadSnapshot(db, tx, repository, number))),
            ("$sourceContent", Fingerprint(source, contentOnly: true)),
            ("$targetContent", Fingerprint(ReadSnapshot(db, tx, repository, number), contentOnly: true)));

    private static void ArchiveConflict(SqliteConnection db, SqliteTransaction tx, string repository, int number,
        string sourceHash, string targetHash, LegacySnapshot source, LegacySnapshot target) =>
        Execute(db, tx, """
            INSERT INTO PrReviewLegacyConflicts (Repository,Number,SourceHash,TargetHash,SourceSnapshot,TargetSnapshot)
            VALUES ($repo,$number,$source,$target,$sourceBytes,$targetBytes)
            ON CONFLICT (Repository,Number,SourceHash,TargetHash) DO NOTHING;
            """, ("$repo", repository), ("$number", number), ("$source", sourceHash), ("$target", targetHash),
            ("$sourceBytes", SerializeSnapshot(source)), ("$targetBytes", SerializeSnapshot(target)));

    private static LegacySnapshot WithLatestLifecycle(LegacySnapshot source, LegacySnapshot target)
    {
        if (source.PullRequest is not { } legacy || target.PullRequest is not { } current) return source;
        if (current[8] is DBNull || (legacy[8] is string checkedAt && ParseUtc(checkedAt) >= ParseUtc((string)current[8]))) return source;
        var row = (object[])legacy.Clone();
        Array.Copy(current, 6, row, 6, 3);
        return source with { PullRequest = row };
    }

    private static LegacySnapshot MapLegacyIds(SqliteConnection db, SqliteTransaction tx, LegacySnapshot source)
    {
        var rows = new List<object[]>();
        foreach (var original in source.Reviews)
        {
            var row = (object[])original.Clone();
            using var map = Command(db, tx, """
                SELECT ReviewId FROM PrReviewLegacyIds WHERE LegacyId=$id AND Repository=$repo AND Number=$number;
                """, ("$id", row[0]), ("$repo", row[1]), ("$number", row[2]));
            if (map.ExecuteScalar() is long id) row[0] = id;
            rows.Add(row);
        }
        return source with { Reviews = rows.OrderBy(r => (long)r[0]).ToList() };
    }

    private static void MergeLifecycle(SqliteConnection db, SqliteTransaction tx, string repository, int number,
        LegacySnapshot source, LegacySnapshot current)
    {
        if (source.PullRequest is not { } legacy || current.PullRequest is not { } target || legacy[8] is not string checkedAt) return;
        if (target[8] is string previous && ParseUtc(previous) >= ParseUtc(checkedAt)) return;
        Execute(db, tx, """
            UPDATE PrReviewPullRequests SET GitHubState=$state,GitHubStateUTC=$stateAt,GitHubCheckedUTC=$checked
            WHERE Repository=$repo AND Number=$number;
            """, ("$repo", repository), ("$number", number), ("$state", legacy[6]), ("$stateAt", legacy[7]), ("$checked", legacy[8]));
    }

    private static LegacySnapshot DeserializeSnapshot(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        object[] ReadRow()
        {
            var values = new object[reader.ReadInt32()];
            for (var i = 0; i < values.Length; i++)
            {
                var kind = reader.ReadString(); var value = reader.ReadString();
                values[i] = kind switch { "null" => DBNull.Value, "Int64" => long.Parse(value, CultureInfo.InvariantCulture), "String" => value,
                    _ => throw new InvalidDataException("Unknown PR history value type.") };
            }
            return values;
        }
        var pr = reader.ReadBoolean() ? ReadRow() : null;
        var reviews = new List<object[]>();
        while (stream.Position < stream.Length) reviews.Add(ReadRow());
        return new(pr, reviews);
    }

    private static PrReviewHistory MapHistory(LegacySnapshot snapshot)
    {
        var reviews = snapshot.Reviews.Select(r => new PrReview((long)r[0], (string)r[1], checked((int)(long)r[2]),
            (string)r[3], (string)r[4], r[5] is DBNull ? null : (string)r[5], (long)r[6] != 0, (string)r[7],
            ParseUtc((string)r[8]), r[9] is DBNull ? null : ParseUtc((string)r[9]), ParseUtc((string)r[10]))).ToList();
        if (snapshot.PullRequest is not { } p) return new(null, reviews);
        var completed = reviews.Where(r => r.Status == PrReviewStatus.Completed).ToList();
        var latest = completed.OrderByDescending(r => r.ReviewedUtc ?? r.StartedUtc).ThenByDescending(r => r.Id).FirstOrDefault();
        return new(new((string)p[0], checked((int)(long)p[1]), (string)p[2], (string)p[3], ParseUtc((string)p[4]), (string)p[5],
            PrTrackerStatus.Derive(reviews.Any(r => r.Status == PrReviewStatus.InProgress), completed.Count, latest?.Blocking ?? false),
            (string)p[6], p[7] is DBNull ? null : ParseUtc((string)p[7]), p[8] is DBNull ? null : ParseUtc((string)p[8]),
            reviews.Count, completed.Count, latest?.HeadSha, latest?.ReviewedUtc, ParseUtc((string)p[9]), ParseUtc((string)p[10])), reviews);
    }
}
