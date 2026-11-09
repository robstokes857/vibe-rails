using System.Globalization;
using System.Text;

namespace TokenSaver.Shape;

/// <summary>How a fully recognized code-mode program emits its test results.</summary>
internal enum TestOutputKind { None, Text, Envelope }

/// <summary>
/// Recognizes a small, complete JavaScript grammar without executing JavaScript. Every tool call
/// must be a literal test command and every emitted value its unmodified result (or output field).
/// Dynamic expressions, interpolation, mutations, mixed batches and unknown statements decline.
/// </summary>
internal static class CodexTestCommands
{
    internal static TestOutputKind Classify(string? code)
    {
        if (code is null || code.Length > 32768 || !Lex(code, out var tokens)) return TestOutputKind.None;
        return new Parser(tokens).Parse();
    }

    private readonly record struct Token(string Value, char Kind);

    private static bool Lex(string code, out List<Token> tokens)
    {
        tokens = [];
        for (var i = 0; i < code.Length;)
        {
            var c = code[i++];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '/' && i < code.Length && code[i] == '/')
            {
                while (i < code.Length && code[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i < code.Length && code[i] == '*')
            {
                var end = code.IndexOf("*/", i + 1, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 2;
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                var quote = c;
                var value = new StringBuilder();
                var closed = false;
                var interpolated = false;
                while (i < code.Length)
                {
                    c = code[i++];
                    if (c == quote) { closed = true; break; }
                    if (c is '\r' or '\n' && quote != '`') return false;
                    if (quote == '`' && c == '$' && i < code.Length && code[i] == '{') interpolated = true;
                    if (c != '\\') { value.Append(c); continue; }
                    if (i == code.Length) return false;
                    c = code[i++];
                    if (c is 'u' or 'x')
                    {
                        var digits = c == 'u' ? 4 : 2;
                        if (i + digits > code.Length || !ushort.TryParse(code.AsSpan(i, digits),
                            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)) return false;
                        value.Append((char)hex);
                        i += digits;
                    }
                    else
                    {
                        var decoded = c switch
                        {
                            'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f',
                            '\\' => '\\', '\'' => '\'', '"' => '"', '`' => '`', '/' => '/', _ => '\0'
                        };
                        if (decoded == '\0') return false;
                        value.Append(decoded);
                    }
                }
                if (!closed) return false;
                tokens.Add(new Token(value.ToString(), interpolated ? 't' : 's'));
            }
            else if (char.IsAsciiLetter(c) || c is '_' or '$')
            {
                var start = i - 1;
                while (i < code.Length && (char.IsAsciiLetterOrDigit(code[i]) || code[i] is '_' or '$')) i++;
                tokens.Add(new Token(code[start..i], 'i'));
            }
            else if (char.IsAsciiDigit(c) || c == '-')
            {
                var start = i - 1;
                while (i < code.Length && (char.IsAsciiDigit(code[i]) || code[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                if (!double.TryParse(code.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
                tokens.Add(new Token(code[start..i], 'n'));
            }
            else if ("(){}[]:;,.=>".Contains(c)) tokens.Add(new Token(c.ToString(), 'p'));
            else return false;
            if (tokens.Count > 4096) return false;
        }
        return true;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;
        private TestOutputKind _kind;
        private readonly HashSet<string> _bindings = new(StringComparer.Ordinal);
        private bool At(string value) => _position < tokens.Count && tokens[_position].Kind != 's' && tokens[_position].Value == value;
        private bool Take(string value) { if (!At(value)) return false; _position++; return true; }
        private bool Identifier(out string name)
        {
            name = "";
            if (_position >= tokens.Count || tokens[_position].Kind != 'i') return false;
            name = tokens[_position++].Value;
            return name is not ("tools" or "text" or "JSON" or "Promise" or "await" or "const" or "let" or "var");
        }

        internal TestOutputKind Parse()
        {
            while (_position < tokens.Count)
            {
                TestOutputKind emitted;
                if (Take("text"))
                {
                    if (!Take("(") || !ReadResult() || !Take(")")) return TestOutputKind.None;
                    emitted = TestOutputKind.Envelope;
                }
                else
                {
                    if (!(Take("const") || Take("let") || Take("var")) || !Identifier(out var binding)
                        || !_bindings.Add(binding) || !Take("=") || !ReadResult()) return TestOutputKind.None;
                    Take(";");
                    emitted = Emit(binding);
                    if (emitted == TestOutputKind.None) return emitted;
                    Take(";");
                    if (At("if") && !SessionMetadata(binding)) return TestOutputKind.None;
                }
                Take(";");
                if (_kind != TestOutputKind.None && _kind != emitted) return TestOutputKind.None;
                _kind = emitted;
            }
            return _kind;
        }

        private bool ReadResult()
        {
            if (!Take("await")) return false;
            if (At("tools")) return ReadCall();
            if (!Take("Promise") || !Take(".") || !(Take("all") || Take("allSettled"))
                || !Take("(") || !Take("[")) return false;
            var count = 0;
            do
            {
                if (At("]")) break;
                if (!ReadCall() || ++count > 32) return false;
            } while (Take(","));
            return count > 0 && Take("]") && Take(")");
        }

        private bool ReadCall()
        {
            if (!Take("tools") || !Take(".")) return false;
            var exec = Take("exec_command");
            if (!exec && !Take("shell_command")) return false;
            if (!Take("(") || !ReadObject(0, exec ? "cmd" : "command", out var command) || !Take(")")) return false;
            return CommandShapes.Classify(command) == CommandShape.TestRun;
        }

        private bool ReadObject(int depth, string? commandKey, out string? command)
        {
            command = null;
            if (depth > 8 || !Take("{")) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (!At("}"))
            {
                if (_position >= tokens.Count || tokens[_position].Kind is not ('s' or 'i')) return false;
                var key = tokens[_position++].Value;
                if (!keys.Add(key) || key == "__proto__" || !Take(":")) return false;
                if (key == commandKey)
                {
                    if (_position >= tokens.Count || tokens[_position].Kind != 's') return false;
                    command = tokens[_position++].Value;
                }
                else if (!ReadLiteral(depth + 1)) return false;
                if (!Take(",")) break;
            }
            return Take("}");
        }

        private bool ReadLiteral(int depth)
        {
            if (depth > 8 || _position >= tokens.Count) return false;
            if (At("{")) return ReadObject(depth, null, out _);
            if (Take("["))
            {
                while (!At("]"))
                {
                    if (!ReadLiteral(depth + 1)) return false;
                    if (!Take(",")) break;
                }
                return Take("]");
            }
            if (tokens[_position].Kind is 's' or 'n') { _position++; return true; }
            return Take("true") || Take("false") || Take("null");
        }

        private TestOutputKind Emit(string binding)
        {
            if (At("text")) return EmitText(binding);
            if (Take("for"))
            {
                if (!Take("(") || !(Take("const") || Take("let")) || !Identifier(out var element)
                    || !Take("of") || !Take(binding) || !Take(")")) return TestOutputKind.None;
                var block = Take("{");
                var emitted = EmitText(element);
                Take(";");
                if (block && !Take("}")) return TestOutputKind.None;
                return emitted;
            }
            if (!Take(binding) || !Take(".") || !Take("forEach") || !Take("(")) return TestOutputKind.None;
            if (Take("text")) return Take(")") ? TestOutputKind.Envelope : TestOutputKind.None;
            var paren = Take("(");
            if (!Identifier(out var parameter) || paren && !Take(")") || !Take("=") || !Take(">")) return TestOutputKind.None;
            var kind = EmitText(parameter);
            return Take(")") ? kind : TestOutputKind.None;
        }

        private TestOutputKind EmitText(string binding)
        {
            if (Take("text"))
            {
                if (!Take("(")) return TestOutputKind.None;
                var stringify = Take("JSON");
                if (stringify && (!Take(".") || !Take("stringify") || !Take("("))) return TestOutputKind.None;
                var kind = TestOutputKind.Envelope;
                if (At("{"))
                {
                    if (!Projection(binding)) return TestOutputKind.None;
                }
                else
                {
                    if (!Take(binding)) return TestOutputKind.None;
                    if (Take("."))
                    {
                        if (!Take("output") || stringify) return TestOutputKind.None;
                        kind = TestOutputKind.Text;
                    }
                }
                if (stringify && !Take(")")) return TestOutputKind.None;
                return Take(")") ? kind : TestOutputKind.None;
            }
            return TestOutputKind.None;
        }

        // Only direct projections of this result's fields; no computed values or unrelated text.
        private bool Projection(string binding)
        {
            if (!Take("{")) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (!At("}"))
            {
                if (_position >= tokens.Count || tokens[_position].Kind is not ('s' or 'i')) return false;
                var key = tokens[_position++].Value;
                if (key is not ("output" or "exit_code" or "session_id" or "wall_time_seconds" or "chunk_id" or "original_token_count")
                    || !keys.Add(key) || !Take(":") || !Take(binding) || !Take(".") || !Take(key)) return false;
                if (!Take(",")) break;
            }
            return keys.Contains("output") && Take("}");
        }

        // A common Codex suffix. Its only emitted text is a labeled session ID, never test output.
        private bool SessionMetadata(string binding)
        {
            if (!Take("if") || !Take("(") || !Take(binding) || !Take(".") || !Take("session_id")
                || !Take(")") || !Take("text") || !Take("(") || _position >= tokens.Count) return false;
            var token = tokens[_position++];
            return token.Kind == 't' && token.Value == "SESSION_ID=${" + binding + ".session_id}" && Take(")");
        }
    }
}
