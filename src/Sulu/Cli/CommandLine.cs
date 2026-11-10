namespace Sulu.Cli;

internal sealed class ParsedCommand
{
    private readonly List<string> _positionals;
    private readonly Dictionary<string, string?> _options;
    private readonly HashSet<string> _flags;

    public ParsedCommand(
        string? name,
        List<string> positionals,
        Dictionary<string, string?> options,
        HashSet<string> flags)
    {
        Name = name;
        _positionals = positionals;
        _options = options;
        _flags = flags;
    }

    public string? Name { get; }

    public IReadOnlyList<string> Positionals => _positionals;

    public IReadOnlyCollection<string> OptionNames => _options.Keys;

    public IReadOnlyCollection<string> FlagNames => _flags;

    public bool HasFlag(string name) => _flags.Contains(name);

    public bool HasOption(string name) => _options.ContainsKey(name);

    public string? GetOption(string name) => _options.TryGetValue(name, out var value) ? value : null;
}

internal static class CommandLine
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "filter",
        "sort",
        "limit",
        "id",
        "index",
        "timeout",
        "settle",
        "backup-dir",
        "to",
        "from",
        "restore",
    };

    private static readonly Dictionary<string, string> ShortOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["h"] = "help",
        ["?"] = "help",
        ["y"] = "yes",
        ["q"] = "quiet",
        ["V"] = "version",
    };

    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "all",
        "json",
        "verbose",
        "yes",
        "quiet",
        "dry-run",
        "no-clean",
        "pause",
        "no-pause",
        "refresh",
        "no-color",
        "include-review",
        "keep-source",
        "registry-only",
        "force",
        "help",
        "version",
    };

    public static ParsedCommand Parse(string[] args)
    {
        string? command = null;
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endOfOptions = false;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (endOfOptions)
            {
                AddPositional(token);
                continue;
            }

            if (token == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var body = token[2..];
                if (body.Length == 0)
                {
                    throw new CliException("Invalid option '--'.", ExitCodes.Usage);
                }

                var equals = body.IndexOf('=');
                var name = equals >= 0 ? body[..equals] : body;
                var inlineValue = equals >= 0 ? body[(equals + 1)..] : null;

                if (ValueOptions.Contains(name))
                {
                    if (inlineValue is not null)
                    {
                        options[name] = inlineValue;
                    }
                    else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options[name] = args[++i];
                    }
                    else
                    {
                        throw new CliException($"Option '--{name}' requires a value.", ExitCodes.Usage);
                    }

                    continue;
                }

                if (!KnownFlags.Contains(name))
                {
                    throw new CliException($"Unknown option '--{name}'.", ExitCodes.Usage);
                }

                if (inlineValue is not null)
                {
                    throw new CliException($"Option '--{name}' does not take a value.", ExitCodes.Usage);
                }

                flags.Add(name);
                continue;
            }

            if (token[0] == '-' && token.Length > 1)
            {
                var shortName = token[1..];
                if (!ShortOptions.TryGetValue(shortName, out var name))
                {
                    throw new CliException($"Unknown option '{token}'.", ExitCodes.Usage);
                }

                flags.Add(name);
                continue;
            }

            AddPositional(token);
        }

        return new ParsedCommand(command, positionals, options, flags);

        void AddPositional(string token)
        {
            if (command is null)
            {
                command = token;
            }
            else
            {
                positionals.Add(token);
            }
        }
    }

    public static void Validate(
        ParsedCommand parsed,
        IReadOnlySet<string> allowedOptions,
        IReadOnlySet<string> allowedFlags)
    {
        foreach (var option in parsed.OptionNames)
        {
            if (!allowedOptions.Contains(option))
            {
                throw new CliException(
                    $"Option '--{option}' is not valid for command '{parsed.Name}'.",
                    ExitCodes.Usage);
            }
        }

        foreach (var flag in parsed.FlagNames)
        {
            if (flag is "help" or "version" or "no-color")
            {
                continue;
            }

            if (!allowedFlags.Contains(flag))
            {
                throw new CliException(
                    $"Option '--{flag}' is not valid for command '{parsed.Name}'.",
                    ExitCodes.Usage);
            }
        }
    }
}
