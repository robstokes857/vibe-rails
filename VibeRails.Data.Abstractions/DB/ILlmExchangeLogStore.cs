using VibeRails.DTOs;
using VibeRails.Services;
using TokenSaver;
using TokenSaver.Pipeline;

namespace VibeRails.DB;


/// <summary>
/// Writes whole relayed request/response pairs (see <see cref="ILlmProxyExchangeSink"/>) so
/// compression opportunities can be judged offline, against whatever the stages happen to be that
/// week rather than against the ones that ran on the day.
///
/// <b>This does not live in state.db, and that is deliberate.</b> One exchange is the entire
/// conversation so far plus the response, and the CLI resends the whole history every turn, so a
/// busy session writes the same growing payload again and again — hundreds of megabytes a day is
/// ordinary. state.db already carries 28M SessionLogs rows and has a documented history of lock
/// contention; adding an unbounded, write-heavy table to it would turn a diagnostic into an
/// outage. Its own file means the log can also be deleted with `rm`, which is the only retention
/// policy this table has.
///
/// Errors are swallowed for the same reason <c>TokenSavingsStore</c> swallows them:
/// this sits on the LLM relay's hot path, and nothing it records is worth failing a request over.
/// </summary>
public interface ILlmExchangeLogStore
{
    /// <summary>Queues one exchange. Never blocks, never throws.</summary>
    void Record(LlmProxyExchange exchange);
}

