namespace SymbolDesigner.Cli;

/// <summary>极简参数解析结果：命名选项字典 + 位置参数列表。
/// 支持 --k=v、--k v、以及不以 -- 开头的 position。零外部依赖。</summary>
internal sealed class ParsedArgs
{
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public List<string> Positionals { get; } = new();
    /// <summary>重复出现的 --set 值（保留每次），供 edit 一次改多字段。</summary>
    public List<string> Sets { get; } = new();

    public bool Has(string key) => Options.ContainsKey(key);
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;
}

/// <summary>极简 "--" 参数解析器（无状态机，仅安全步进）。</summary>
internal static class Args
{
    public static ParsedArgs Parse(string[] tokens)
    {
        var r = new ParsedArgs();
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];
            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                string body = t[2..];
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    string k = body[..eq];
                    string v = body[(eq + 1)..];
                    if (k.Length == 0) throw new ArgError($"非法选项: {t}");
                    Record(r, k, v, isSet: false);
                }
                else
                {
                    // --k 后面跟值（下一 token）；否认为布尔开关
                    if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        Record(r, body, tokens[++i], isSet: body == "set");
                    }
                    else
                    {
                        Record(r, body, "", isSet: body == "set");
                    }
                }
            }
            else
            {
                r.Positionals.Add(t);
            }
        }
        return r;
    }

    private static void Record(ParsedArgs r, string key, string value, bool isSet)
    {
        if (isSet && value.Length > 0)
            r.Sets.Add(value);
        r.Options[key] = value;
    }
}

/// <summary>参数/用法错误（对应退出码 1）。</summary>
internal sealed class ArgError : Exception
{
    public ArgError(string message) : base(message) { }
}