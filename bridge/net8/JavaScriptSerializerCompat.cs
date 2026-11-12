// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F3a: System.Web.Script.Serialization.JavaScriptSerializer for the .NET 8 build of Bridge.
// System.Web.Extensions does not exist in .NET 8; the Bridge sources (net48 release path) keep using
// JavaScriptSerializer, and this file gives the .NET 8 build the same behaviour, measured on the .NET Framework 4.8
// implementation (tests: bridge/net8/tests/JsonCompatTests.cs, golden output of net48 in tests/json-golden.txt):
//
// Deserialize
//   - raw tree: objects -> Dictionary<string, object>, arrays -> ArrayList, numbers -> int, long, decimal, double
//     (first that parses), "\/Date(ms)\/" -> DateTime (UTC), null/true/false; empty input -> null.
//   - lenient like the original: single-quoted strings, unquoted member names, "+1", "01", ".5", NaN;
//     comments, trailing commas, unknown escapes and trailing text are ArgumentException.
//   - every value counts one level of RecursionLimit (default 100); input longer than MaxJsonLength fails.
//   - conversion to a type: object/null/IDictionary<string,object> -> converted tree (arrays object[]);
//     Dictionary<string, object> and ArrayList -> the raw tree as is (nested arrays stay ArrayList);
//     Dictionary<string, T>, List<T>/IList<T>/IEnumerable<T>, T[]; classes by public settable property or public
//     field, name case-insensitive, unknown members ignored, [ScriptIgnore] not looked at;
//     scalars through TypeDescriptor converters (directly, else through the invariant string), null to a value type
//     is InvalidOperationException.
// Serialize
//   - public fields first, then readable public properties without [ScriptIgnore]; IDictionary as an object,
//     IEnumerable as an array; enums as numbers; DateTime as "\/Date(ms)\/" (UTC); double/float "R"; decimal and
//     integers invariant; char '\0' -> null; Guid, Uri as strings; NaN/Infinity as bare words (as net48).
//   - strings: \" \\ \b \f \n \r \t, \uXXXX (lower-case hex) for other control chars, < > & ' U+0085 U+2028 U+2029.
//   - circular references are InvalidOperationException.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace System.Web.Script.Serialization
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class ScriptIgnoreAttribute : Attribute
    {
        public bool ApplyToOverrides { get; set; }
    }

    public class JavaScriptSerializer
    {
        internal const int DefaultMaxJsonLength = 2097152;
        internal const int DefaultRecursionLimit = 100;

        private static readonly long DatetimeMinTimeTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private int _maxJsonLength = DefaultMaxJsonLength;
        private int _recursionLimit = DefaultRecursionLimit;

        public int MaxJsonLength
        {
            get { return _maxJsonLength; }
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException("value");
                _maxJsonLength = value;
            }
        }

        public int RecursionLimit
        {
            get { return _recursionLimit; }
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException("value");
                _recursionLimit = value;
            }
        }

        // ------------------------------------------------------------------ deserialize

        public object DeserializeObject(string input)
        {
            return ConvertTo(Parse(input), null);
        }

        public T Deserialize<T>(string input)
        {
            return (T)ConvertTo(Parse(input), typeof(T));
        }

        public object Deserialize(string input, Type targetType)
        {
            return ConvertTo(Parse(input), targetType);
        }

        public T ConvertToType<T>(object obj)
        {
            return (T)ConvertTo(obj, typeof(T));
        }

        private object Parse(string input)
        {
            if (input == null)
                throw new ArgumentNullException("input");
            if (input.Length > _maxJsonLength)
                throw new ArgumentException("Error during serialization or deserialization using the JSON JavaScriptSerializer. The length of the string exceeds the value set on the maxJsonLength property.");
            return new JsonReader(input, _recursionLimit).ReadDocument();
        }

        internal static object ConvertTo(object o, Type type)
        {
            if (type == typeof(object))
                type = null;

            if (o == null)
            {
                if (type == null)
                    return null;
                if (type == typeof(char))
                    return '\0';
                if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
                    throw new InvalidOperationException("Cannot convert null to a value type.");
                return null;
            }

            Dictionary<string, object> dict = o as Dictionary<string, object>;
            if (dict != null)
                return ConvertDictionary(dict, type);

            ArrayList list = o as ArrayList;
            if (list != null)
                return ConvertList(list, type);

            if (type == null || o.GetType() == type)
                return o;

            Type underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                if (o.GetType() == underlying)
                    return o;
                type = underlying;
            }

            TypeConverter converter = TypeDescriptor.GetConverter(type);
            if (converter.CanConvertFrom(o.GetType()))
                return converter.ConvertFrom(null, CultureInfo.InvariantCulture, o);
            if (converter.CanConvertFrom(typeof(string)))
            {
                string text = TypeDescriptor.GetConverter(o).ConvertToInvariantString(o);
                return converter.ConvertFromInvariantString(text);
            }
            if (type.IsAssignableFrom(o.GetType()))
                return o;
            throw new InvalidOperationException("Cannot convert object of type '" + o.GetType() + "' to type '" + type + "'");
        }

        private static object ConvertDictionary(Dictionary<string, object> dict, Type type)
        {
            if (type == typeof(Dictionary<string, object>))
                return dict;

            if (type == null || type == typeof(IDictionary<string, object>) || type == typeof(IDictionary))
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in dict)
                    result[kv.Key] = ConvertTo(kv.Value, null);
                return result;
            }

            if (type == typeof(Hashtable))
            {
                Hashtable ht = new Hashtable();
                foreach (KeyValuePair<string, object> kv in dict)
                    ht[kv.Key] = ConvertTo(kv.Value, null);
                return ht;
            }

            Type valueType = GenericDictionaryValueType(type);
            if (valueType != null)
            {
                IDictionary result = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType));
                foreach (KeyValuePair<string, object> kv in dict)
                    result[kv.Key] = ConvertTo(kv.Value, valueType);
                return result;
            }

            if (type.IsAbstract || type.IsInterface || (!type.IsValueType && type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) == null))
                throw new InvalidOperationException("Cannot convert object of type '" + dict.GetType() + "' to type '" + type + "'");

            object target = Activator.CreateInstance(type);
            foreach (KeyValuePair<string, object> kv in dict)
                AssignMember(target, type, kv.Key, kv.Value);
            return target;
        }

        // Dictionary<string, T>, IDictionary<string, T>, IReadOnlyDictionary<string, T> -> T.
        private static Type GenericDictionaryValueType(Type type)
        {
            if (!type.IsGenericType)
                return null;
            Type def = type.GetGenericTypeDefinition();
            if (def != typeof(Dictionary<,>) && def != typeof(IDictionary<,>) && def != typeof(IReadOnlyDictionary<,>))
                return null;
            Type[] args = type.GetGenericArguments();
            if (args[0] != typeof(string) && args[0] != typeof(object))
                throw new InvalidOperationException("Type '" + type + "' is not supported for serialization/deserialization of a dictionary, keys must be strings or objects.");
            return args[1];
        }

        private static void AssignMember(object target, Type type, string name, object value)
        {
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                MethodInfo setter = prop.GetSetMethod();
                if (setter != null)
                {
                    setter.Invoke(target, new object[] { ConvertTo(value, prop.PropertyType) });
                    return;
                }
            }
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (field != null && !field.IsInitOnly)
                field.SetValue(target, ConvertTo(value, field.FieldType));
        }

        private static object ConvertList(ArrayList list, Type type)
        {
            if (type == typeof(ArrayList))
                return list;

            if (type == null)
            {
                object[] result = new object[list.Count];
                for (int i = 0; i < list.Count; i++)
                    result[i] = ConvertTo(list[i], null);
                return result;
            }

            if (type.IsArray)
            {
                Type element = type.GetElementType();
                Array result = Array.CreateInstance(element, list.Count);
                for (int i = 0; i < list.Count; i++)
                    result.SetValue(ConvertTo(list[i], element), i);
                return result;
            }

            if (type == typeof(IList) || type == typeof(ICollection) || type == typeof(IEnumerable))
            {
                ArrayList result = new ArrayList(list.Count);
                foreach (object item in list)
                    result.Add(ConvertTo(item, null));
                return result;
            }

            if (type.IsGenericType)
            {
                Type def = type.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
                    || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
                {
                    Type element = type.GetGenericArguments()[0];
                    IList result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
                    foreach (object item in list)
                        result.Add(ConvertTo(item, element));
                    return result;
                }
            }

            throw new InvalidOperationException("Type '" + type + "' is not supported for deserialization of an array.");
        }

        // ------------------------------------------------------------------ serialize

        public string Serialize(object obj)
        {
            StringBuilder sb = new StringBuilder();
            Serialize(obj, sb);
            return sb.ToString();
        }

        public void Serialize(object obj, StringBuilder output)
        {
            int start = output.Length;
            WriteValue(output, obj, new HashSet<object>(ReferenceEqualityComparer.Instance));
            if (output.Length - start > _maxJsonLength)
                throw new InvalidOperationException("Error during serialization or deserialization using the JSON JavaScriptSerializer. The length of the string exceeds the value set on the maxJsonLength property.");
        }

        private static void WriteValue(StringBuilder sb, object o, HashSet<object> inUse)
        {
            if (o == null || o is DBNull)
            {
                sb.Append("null");
                return;
            }

            string s = o as string;
            if (s != null)
            {
                WriteString(sb, s);
                return;
            }

            if (o is char)
            {
                if ((char)o == '\0')
                    sb.Append("null");
                else
                    WriteString(sb, o.ToString());
                return;
            }

            if (o is bool)
            {
                sb.Append((bool)o ? "true" : "false");
                return;
            }

            if (o is DateTime)
            {
                DateTime dt = (DateTime)o;
                sb.Append("\"\\/Date(");
                sb.Append(((dt.ToUniversalTime().Ticks - DatetimeMinTimeTicks) / 10000).ToString(CultureInfo.InvariantCulture));
                sb.Append(")\\/\"");
                return;
            }

            if (o is Guid)
            {
                sb.Append('"').Append(((Guid)o).ToString()).Append('"');
                return;
            }

            Uri uri = o as Uri;
            if (uri != null)
            {
                WriteString(sb, uri.GetComponents(UriComponents.SerializationInfoString, UriFormat.UriEscaped));
                return;
            }

            if (o is double)
            {
                sb.Append(FormatR((double)o));
                return;
            }

            if (o is float)
            {
                sb.Append(FormatR((float)o));
                return;
            }

            Type type = o.GetType();
            if (type.IsEnum)
            {
                sb.Append(((Enum)o).ToString("D"));
                return;
            }

            if (type.IsPrimitive || o is decimal)
            {
                sb.Append(((IConvertible)o).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (!type.IsValueType)
            {
                if (inUse.Contains(o))
                    throw new InvalidOperationException("A circular reference was detected while serializing an object of type '" + type.FullName + "'.");
                inUse.Add(o);
            }
            try
            {
                IDictionary dict = o as IDictionary;
                if (dict != null)
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first)
                            sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        WriteValue(sb, e.Value, inUse);
                    }
                    sb.Append('}');
                    return;
                }

                IEnumerable seq = o as IEnumerable;
                if (seq != null)
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in seq)
                    {
                        if (!first)
                            sb.Append(',');
                        first = false;
                        WriteValue(sb, item, inUse);
                    }
                    sb.Append(']');
                    return;
                }

                WriteObject(sb, o, type, inUse);
            }
            finally
            {
                if (!type.IsValueType)
                    inUse.Remove(o);
            }
        }

        private static void WriteObject(StringBuilder sb, object o, Type type, HashSet<object> inUse)
        {
            sb.Append('{');
            bool first = true;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsDefined(typeof(ScriptIgnoreAttribute), true))
                    continue;
                if (!first)
                    sb.Append(',');
                first = false;
                WriteString(sb, field.Name);
                sb.Append(':');
                WriteValue(sb, field.GetValue(o), inUse);
            }
            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0 || prop.GetGetMethod() == null)
                    continue;
                if (prop.IsDefined(typeof(ScriptIgnoreAttribute), true))
                    continue;
                if (!first)
                    sb.Append(',');
                first = false;
                WriteString(sb, prop.Name);
                sb.Append(':');
                WriteValue(sb, prop.GetValue(o, null), inUse);
            }
            sb.Append('}');
        }

        // .NET Framework "R": 15 significant digits when they parse back to the same value, else 17 (.NET Core 3.0+
        // prints the shortest round-trip form instead: 1/3 is 0.3333333333333333 there, 0.33333333333333331 here);
        // negative zero prints as 0.
        internal static string FormatR(double d)
        {
            if (d == 0)
                return "0";
            if (Double.IsNaN(d) || Double.IsInfinity(d))
                return d.ToString(CultureInfo.InvariantCulture);
            string s = d.ToString("G15", CultureInfo.InvariantCulture);
            if (Double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) != d)
                s = d.ToString("G17", CultureInfo.InvariantCulture);
            return s;
        }

        internal static string FormatR(float f)
        {
            if (f == 0)
                return "0";
            if (Single.IsNaN(f) || Single.IsInfinity(f))
                return f.ToString(CultureInfo.InvariantCulture);
            string s = f.ToString("G7", CultureInfo.InvariantCulture);
            if (Single.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) != f)
                s = f.ToString("G9", CultureInfo.InvariantCulture);
            return s;
        }

        internal static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '<' || c == '>' || c == '&' || c == '\'' || c == '\u0085' || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------ reader

        private sealed class JsonReader
        {
            private static readonly Regex DateLiteral = new Regex(
                @"\A\\/Date\((?<ms>-?[0-9]+)(?:[a-zA-Z]|(?:\+|-)[0-9]{4})?\)\\/\z", RegexOptions.CultureInvariant);

            private readonly string _s;
            private readonly int _limit;
            private int _pos;

            public JsonReader(string s, int limit)
            {
                _s = s;
                _limit = limit;
            }

            public object ReadDocument()
            {
                SkipWhite();
                if (_pos >= _s.Length)
                    return null;
                object value = ReadValue(0);
                SkipWhite();
                if (_pos < _s.Length)
                    throw Error("Invalid JSON primitive");
                return value;
            }

            private ArgumentException Error(string what)
            {
                return new ArgumentException(what + " at position " + _pos.ToString(CultureInfo.InvariantCulture) + ".");
            }

            private void SkipWhite()
            {
                while (_pos < _s.Length && Char.IsWhiteSpace(_s[_pos]))
                    _pos++;
            }

            private object ReadValue(int depth)
            {
                depth++;
                if (depth > _limit)
                    throw new ArgumentException("RecursionLimit exceeded.");
                SkipWhite();
                if (_pos >= _s.Length)
                    throw Error("Unexpected end of input");
                char c = _s[_pos];
                if (c == '{')
                    return ReadObject(depth);
                if (c == '[')
                    return ReadArray(depth);
                if (c == '"' || c == '\'')
                    return ReadStringValue();
                return ReadPrimitive();
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                _pos++; // {
                SkipWhite();
                if (_pos < _s.Length && _s[_pos] == '}')
                {
                    _pos++;
                    return result;
                }
                while (true)
                {
                    SkipWhite();
                    if (_pos >= _s.Length)
                        throw Error("Unterminated object");
                    string name;
                    char c = _s[_pos];
                    if (c == '"' || c == '\'')
                    {
                        string raw;
                        name = ReadString(out raw);
                    }
                    else
                    {
                        int start = _pos;
                        while (_pos < _s.Length && (Char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_' || _s[_pos] == '$'))
                            _pos++;
                        if (_pos == start)
                            throw Error("Invalid object passed in, member name expected");
                        name = _s.Substring(start, _pos - start);
                    }
                    SkipWhite();
                    if (_pos >= _s.Length || _s[_pos] != ':')
                        throw Error("Invalid object passed in, ':' expected");
                    _pos++;
                    result[name] = ReadValue(depth);
                    SkipWhite();
                    if (_pos >= _s.Length)
                        throw Error("Unterminated object");
                    if (_s[_pos] == ',')
                    {
                        _pos++;
                        SkipWhite();
                        if (_pos < _s.Length && _s[_pos] == '}')
                            throw Error("Invalid object passed in, member name expected");
                        continue;
                    }
                    if (_s[_pos] == '}')
                    {
                        _pos++;
                        return result;
                    }
                    throw Error("Invalid object passed in, ',' or '}' expected");
                }
            }

            private ArrayList ReadArray(int depth)
            {
                ArrayList result = new ArrayList();
                _pos++; // [
                SkipWhite();
                if (_pos < _s.Length && _s[_pos] == ']')
                {
                    _pos++;
                    return result;
                }
                while (true)
                {
                    result.Add(ReadValue(depth));
                    SkipWhite();
                    if (_pos >= _s.Length)
                        throw Error("Unterminated array");
                    if (_s[_pos] == ',')
                    {
                        _pos++;
                        SkipWhite();
                        if (_pos < _s.Length && _s[_pos] == ']')
                            throw Error("Invalid array passed in, value expected");
                        continue;
                    }
                    if (_s[_pos] == ']')
                    {
                        _pos++;
                        return result;
                    }
                    throw Error("Invalid array passed in, ',' or ']' expected");
                }
            }

            private object ReadStringValue()
            {
                string raw;
                string value = ReadString(out raw);
                Match m = DateLiteral.Match(raw);
                if (m.Success)
                {
                    long ms;
                    if (Int64.TryParse(m.Groups["ms"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
                        return new DateTime(ms * 10000 + DatetimeMinTimeTicks, DateTimeKind.Utc);
                }
                return value;
            }

            // raw: the literal between the quotes, escapes not decoded (the \/Date(...)\/ form is matched on it).
            private string ReadString(out string raw)
            {
                char quote = _s[_pos++];
                int start = _pos;
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (_pos >= _s.Length)
                        throw Error("Unterminated string passed in");
                    char c = _s[_pos++];
                    if (c == quote)
                    {
                        raw = _s.Substring(start, _pos - 1 - start);
                        return sb.ToString();
                    }
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (_pos >= _s.Length)
                        throw Error("Unterminated string passed in");
                    char e = _s[_pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            int code;
                            if (_pos + 4 > _s.Length || !Int32.TryParse(_s.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw Error("Unrecognized escape sequence");
                            sb.Append((char)code);
                            _pos += 4;
                            break;
                        default:
                            throw Error("Unrecognized escape sequence");
                    }
                }
            }

            private object ReadPrimitive()
            {
                int start = _pos;
                while (_pos < _s.Length)
                {
                    char c = _s[_pos];
                    if (c == ',' || c == '}' || c == ']' || c == ':' || Char.IsWhiteSpace(c) || c == '{' || c == '[' || c == '"' || c == '\'')
                        break;
                    _pos++;
                }
                string token = _s.Substring(start, _pos - start);
                if (token.Length == 0)
                    throw Error("Invalid JSON primitive");
                if (token == "null")
                    return null;
                if (token == "true")
                    return true;
                if (token == "false")
                    return false;

                int i;
                if (Int32.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                    return i;
                long l;
                if (Int64.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    return l;
                decimal m;
                if (Decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out m))
                    return m;
                double d;
                if (Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return d;
                _pos = start;
                throw Error("Invalid JSON primitive: " + token);
            }
        }
    }
}
