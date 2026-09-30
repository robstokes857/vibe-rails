using System.Globalization;
using Microsoft.Data.Sqlite;

using VibeRails.Data.Replay;

namespace VibeRails.Data.Sqlite.Replay;

// Deliberately independent of the product repository's initialization/migration services.
public sealed class ReplayStore : IReplayStore
{
    private readonly string state;
    private readonly string proxy;
    public ReplayStore(SqliteStoragePaths paths)
    {
        state = paths.StatePath;
        proxy = paths.ProxyDatabasePath;
    }
    public AppStatus Status() => new([new("Terminal and code", File.Exists(state)), new("Proxy captures", File.Exists(proxy))]);
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON;";
        command.ExecuteNonQuery();
        return connection;
    }
    private static SqliteCommand Query(SqliteConnection connection, string sql, params (string Key, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static int? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static long Time(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToUnixTimeMilliseconds() : 0;
    private const string SessionSelect = """
        SELECT s.Id,s.Cli,s.EnvironmentName,s.WorkingDirectory,s.ProjectDisplayName,s.SessionDisplayName,s.StartedUTC,s.EndedUTC,s.ExitCode,
        EXISTS(SELECT 1 FROM SessionLogs l WHERE l.SessionId=s.Id) OR EXISTS(SELECT 1 FROM TerminalSessionLogs t WHERE t.SessionId=s.Id),
        (SELECT count(*) FROM UserInputs u JOIN InputFileChanges f ON f.UserInputId=u.Id WHERE u.SessionId=s.Id)
        FROM Sessions s
        """;
    private static SessionInfo ReadSession(SqliteDataReader r) => new(S(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),Time(S(r,6)),r.IsDBNull(7)?null:Time(S(r,7)),N(r,8),r.GetBoolean(9),r.GetInt32(10));
    public SessionPage Sessions(string? search, int offset)
    {
        using var db = Open(state);
        using var command = Query(db, SessionSelect + " WHERE ($q='' OR s.Id LIKE $pattern OR s.WorkingDirectory LIKE $pattern OR s.SessionDisplayName LIKE $pattern OR s.Cli LIKE $pattern) ORDER BY s.StartedUTC DESC LIMIT 40 OFFSET $offset", ("$q", search ?? ""), ("$pattern", "%" + search + "%"), ("$offset", offset));
        using var reader = command.ExecuteReader();
        List<SessionInfo> sessions = [];
        while (reader.Read()) sessions.Add(ReadSession(reader));
        return new(sessions, sessions.Count == 40 ? offset + 40 : -1);
    }
    public Manifest? Manifest(string id)
    {
        using var db = Open(state);
        using var command = Query(db, SessionSelect + " WHERE s.Id=$id", ("$id", id));
        SessionInfo session;
        using (var reader = command.ExecuteReader()) { if (!reader.Read()) return null; session = ReadSession(reader); }
        List<string> notes = [];
        List<Prompt> prompts = [];
        using (var c = Query(db, "SELECT Id,Sequence,TimestampUTC,InputText FROM UserInputs WHERE SessionId=$id ORDER BY Sequence,Id", ("$id",id)))
        using (var r = c.ExecuteReader()) while(r.Read()) prompts.Add(new(r.GetInt64(0),r.GetInt32(1),Time(S(r,2)),S(r,3)));
        List<Geometry> geometry = [];
        using(var c = Query(db,"SELECT Id,Sequence,Timestamp,Cols,Rows,length(Data) FROM TerminalSessionLogs WHERE SessionId=$id ORDER BY Sequence,Id",("$id",id)))
        using(var r = c.ExecuteReader()) while(r.Read()) geometry.Add(new(r.GetInt64(0),r.GetInt32(1),Time(S(r,2)),r.GetInt32(3),r.GetInt32(4),r.GetInt64(5)));
        var source = "raw";
        long maxId, count, bytes, end;
        using(var c = Query(db,"SELECT coalesce(max(Id),0),count(*),coalesce(sum(length(Content)),0),max(Timestamp) FROM SessionLogs WHERE SessionId=$id",("$id",id)))
        using(var r = c.ExecuteReader()) { r.Read(); maxId=r.GetInt64(0); count=r.GetInt64(1); bytes=r.GetInt64(2); end=Time(S(r,3)); }
        if(count==0 && geometry.Count>0)
        {
            source="enriched"; maxId=geometry.Max(x=>x.Id); count=geometry.Count; bytes=geometry.Sum(x=>x.Bytes); end=geometry.Max(x=>x.At);
            notes.Add("Only buffered terminal frames are available; their timestamps mark buffer flushes.");
        }
        if(source=="raw" && geometry.Sum(x=>x.Bytes)!=bytes)
            notes.Add("Raw and buffered terminal byte totals differ. Resize alignment uses the captured byte offsets and may be incomplete.");
        if(geometry.Count==0 && count>0) notes.Add("No terminal dimensions were captured. Using a 120 × 30 grid.");
        long proxyMax = 0;
        if(File.Exists(proxy))
        {
            using var p=Open(proxy);
            using var c=Query(p,"SELECT coalesce(max(rowid),0),max(CreatedUTC) FROM ProxyExchanges WHERE SessionId=$id",("$id",id));
            using var r=c.ExecuteReader(); r.Read(); proxyMax=r.GetInt64(0); end=Math.Max(end,Time(S(r,1)));
        }
        else notes.Add("The proxy database is unavailable; terminal and code history are still available.");
        end=Math.Max(end,session.Ended ?? session.Started);
        if(prompts.Count>0) end=Math.Max(end,prompts.Max(x=>x.At));
        List<Change> changes=[];
        using(var c=Query(db,"SELECT f.Id,f.UserInputId,f.PreviousInputId,f.FilePath,f.ChangeType,f.LinesAdded,f.LinesDeleted,length(f.DiffContent) FROM InputFileChanges f JOIN UserInputs u ON u.Id=f.UserInputId WHERE u.SessionId=$id ORDER BY u.Sequence,f.Id",("$id",id)))
        using(var r=c.ExecuteReader()) while(r.Read())
        {
            var inputId=r.GetInt64(1);
            var index=prompts.FindIndex(x=>x.Id==inputId);
            // Old captures describe the previous input interval; current captures belong to this prompt's window.
            var legacy=!r.IsDBNull(2);
            var at=legacy ? prompts[index].At : index+1<prompts.Count ? prompts[index+1].At : end;
            changes.Add(new(r.GetInt64(0),inputId,S(r,3),S(r,4),N(r,5),N(r,6),!r.IsDBNull(7)&&r.GetInt64(7)>0,at,legacy?"Previous prompt window":index+1<prompts.Count?"At next prompt boundary":"At end of recording snapshot"));
        }
        List<BoardLink> cards=[];
        if(changes.Count>0) notes.Add("Code captures cover prompt windows. Exact edit times and complete file snapshots were not recorded; the viewer shows the saved patches.");
        if(session.Ended is null) notes.Add("This is a snapshot of an open session. Reload to include newly captured activity.");
        return new(session,cards,prompts,changes,geometry,source,maxId,proxyMax,count,bytes,end,notes);
    }
    public FramePage Frames(string id,long after,long max,string source)
    {
        using var db=Open(state);
        var enriched=source=="enriched";
        using var c=Query(db,enriched?
            "SELECT Id,Timestamp,Data,Cols,Rows FROM TerminalSessionLogs WHERE SessionId=$id AND Id>$after AND Id<=$max ORDER BY Id LIMIT 2000":
            "SELECT Id,Timestamp,Content,0,0 FROM SessionLogs WHERE SessionId=$id AND Id>$after AND Id<=$max ORDER BY Id LIMIT 2000",("$id",id),("$after",after),("$max",max));
        using var r=c.ExecuteReader();
        List<Frame> frames=[]; long size=0;
        while(r.Read()) { var data=(byte[])r[2]; frames.Add(new(r.GetInt64(0),Time(S(r,1)),data,r.GetInt32(3),r.GetInt32(4))); size+=data.Length; if(size>=4*1024*1024) break; }
        var next=frames.Count>0?frames[^1].Id:after;
        return new(frames,next,frames.Count==0||next>=max);
    }
    public ExchangePage Exchanges(string id,long after,long max)
    {
        if(!File.Exists(proxy)) return new([],after,true);
        using var db=Open(proxy);
        using var c=Query(db,"""
            SELECT rowid,Id,CreatedUTC,Provider,Method,Path,StatusCode,ElapsedMs,ResponseTruncated,ResponseBody,
            CASE WHEN json_valid(RequestBefore) THEN coalesce(json_extract(RequestBefore,'$.model'),'') ELSE '' END,
            CASE WHEN json_valid(RequestBefore) THEN coalesce(json_extract(RequestBefore,'$.reasoning.effort'),json_extract(RequestBefore,'$.output_config.effort'),json_extract(RequestBefore,'$.thinking.type'),'') ELSE '' END
            FROM ProxyExchanges WHERE SessionId=$id AND rowid>$after AND rowid<=$max ORDER BY rowid LIMIT 30
            """,("$id",id),("$after",after),("$max",max));
        using var r=c.ExecuteReader(); List<Exchange> exchanges=[];
        while(r.Read())
        {
            var parsed=ToolParser.Parse(S(r,9));
            exchanges.Add(new(r.GetInt64(0),S(r,1),Time(S(r,2)),S(r,3),S(r,4),S(r,5),r.GetInt32(6),r.GetInt32(7),r.GetBoolean(8),S(r,10),S(r,11),parsed.Tools,parsed.Note));
        }
        var next=exchanges.Count>0?exchanges[^1].Cursor:after;
        return new(exchanges,next,exchanges.Count==0||next>=max);
    }
    public DiffDetail? Diff(string id,long changeId)
    {
        using var db=Open(state);
        using var c=Query(db,"SELECT f.Id,f.FilePath,f.DiffContent FROM InputFileChanges f JOIN UserInputs u ON u.Id=f.UserInputId WHERE u.SessionId=$id AND f.Id=$change",("$id",id),("$change",changeId));
        using var r=c.ExecuteReader();return r.Read()?new(r.GetInt64(0),S(r,1),r.IsDBNull(2)?null:S(r,2)):null;
    }
    public ExchangeDetail? ExchangeDetail(string id,string exchangeId)
    {
        if(!File.Exists(proxy)) return null;
        using var db=Open(proxy);
        using var c=Query(db,"""
            SELECT Id,substr(RequestBefore,1,2000000),substr(RequestAfter,1,2000000),substr(ResponseBody,1,2000000),
            max(length(RequestBefore),length(RequestAfter),length(ResponseBody))>2000000,ResponseTruncated
            FROM ProxyExchanges WHERE SessionId=$id AND Id=$exchange
            """,("$id",id),("$exchange",exchangeId));
        using var r=c.ExecuteReader();return r.Read()?new(S(r,0),S(r,1),S(r,2),S(r,3),r.GetBoolean(4),r.GetBoolean(5)):null;
    }
}
