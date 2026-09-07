using System.Text;
using MoonSharp.Interpreter;

namespace MetroidvaniaStudio.Scripting;

public static partial class LuaScriptRunner
{
    private sealed partial class Execution
    {
        private void InstallLibraries()
        {
            var strings = new Table(script);
            strings.Set("len", Callback(args => DynValue.NewNumber(Text(args[0]).Length)));
            strings.Set("lower", Callback(args => DynValue.NewString(Text(args[0]).ToLowerInvariant())));
            strings.Set("upper", Callback(args => DynValue.NewString(Text(args[0]).ToUpperInvariant())));
            strings.Set("reverse", Callback(args => DynValue.NewString(new string(Text(args[0]).Reverse().ToArray()))));
            strings.Set("sub", Callback(args =>
            {
                string text = Text(args[0]);
                long first = Integer(args[1]);
                long last = args.Count > 2 ? Integer(args[2]) : -1;
                first = first < 0 ? text.Length + first + 1 : first;
                last = last < 0 ? text.Length + last + 1 : last;
                first = Math.Max(1, first);
                last = Math.Min(text.Length, last);
                return DynValue.NewString(first > last ? "" : text.Substring((int)first - 1, (int)(last - first + 1)));
            }));
            strings.Set("rep", Callback(args =>
            {
                string text = Text(args[0]);
                int count = Integer(args[1]);
                string separator = args.Count > 2 ? Text(args[2]) : "";
                if (count <= 0) return DynValue.NewString("");
                long length = (long)text.Length * count + (long)separator.Length * (count - 1);
                if (length > MaximumStringLength) throw new LuaScriptException("Repeated string exceeds 65536 characters.");
                if (length == 0) return DynValue.NewString("");
                var result = new StringBuilder((int)length);
                for (int i = 0; i < count; i++)
                {
                    if (i != 0) result.Append(separator);
                    result.Append(text);
                }
                return DynValue.NewString(result.ToString());
            }));
            strings.Set("find", Callback(args =>
            {
                string text = Text(args[0]), search = Text(args[1]);
                long start = args.Count > 2 ? Integer(args[2]) : 1;
                if (args.Count > 3 && !args[3].CastToBool()) throw new LuaScriptException("string.find supports plain text only.");
                start = start < 0 ? text.Length + start + 1 : start;
                start = Math.Max(1, start);
                if (start > text.Length + 1L) return DynValue.Nil;
                int found = text.IndexOf(search, (int)start - 1, StringComparison.Ordinal);
                return found < 0 ? DynValue.Nil : DynValue.NewTuple(DynValue.NewNumber(found + 1), DynValue.NewNumber(found + search.Length));
            }));
            script.Globals.Set("string", DynValue.NewTable(strings));

            var tables = new Table(script);
            tables.Set("insert", Callback(args =>
            {
                Table table = Sequence(args[0]);
                if (table.Length >= 100_000) throw new LuaScriptException("Table sequence exceeds 100000 items.");
                int position = args.Count == 2 ? table.Length + 1 : Integer(args[1]);
                if (args.Count < 2 || args.Count > 3 || position < 1 || position > table.Length + 1)
                    throw new LuaScriptException("table.insert position is outside the sequence.");
                for (int i = table.Length; i >= position; i--) table.Set(i + 1, table.Get(i));
                table.Set(position, args[args.Count - 1]);
                return DynValue.Nil;
            }));
            tables.Set("remove", Callback(args =>
            {
                Table table = Sequence(args[0]);
                int position = args.Count > 1 ? Integer(args[1]) : table.Length;
                if (position < 1 || position > table.Length) return DynValue.Nil;
                DynValue removed = table.Get(position);
                int length = table.Length;
                for (int i = position; i < length; i++) table.Set(i, table.Get(i + 1));
                table.Set(length, DynValue.Nil);
                return removed;
            }));
            tables.Set("concat", Callback(args =>
            {
                Table table = Sequence(args[0]);
                string separator = args.Count > 1 ? Text(args[1]) : "";
                int first = args.Count > 2 ? Integer(args[2]) : 1;
                int last = args.Count > 3 ? Integer(args[3]) : table.Length;
                if (first < 1 || last > table.Length) throw new LuaScriptException("table.concat range is outside the sequence.");
                var result = new StringBuilder();
                for (int i = first; i <= last; i++)
                {
                    string value = Text(table.Get(i));
                    if ((long)result.Length + value.Length + (i > first ? separator.Length : 0) > MaximumStringLength)
                        throw new LuaScriptException("Concatenated string exceeds 65536 characters.");
                    if (i > first) result.Append(separator);
                    result.Append(value);
                }
                return DynValue.NewString(result.ToString());
            }));
            script.Globals.Set("table", DynValue.NewTable(tables));
        }

        private static Table Sequence(DynValue value)
        {
            if (value.Type != DataType.Table) throw new LuaScriptException("Expected a table sequence.");
            if (value.Table.Length > 100_000) throw new LuaScriptException("Table sequence exceeds 100000 items.");
            return value.Table;
        }
    }
}
