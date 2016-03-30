using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ServiceStack.Reflection;
using ServiceStack.Text.Common;
using ServiceStack.Text.Jsv;

namespace ServiceStack.Text
{
    public class CsvSerializer
    {
        public static UTF8Encoding UTF8Encoding = new UTF8Encoding(false); //Don't emit UTF8 BOM by default

        private static Dictionary<Type, WriteObjectDelegate> WriteFnCache = new Dictionary<Type, WriteObjectDelegate>();
        internal static WriteObjectDelegate GetWriteFn(Type type)
        {
            try
            {
                WriteObjectDelegate writeFn;
                if (WriteFnCache.TryGetValue(type, out writeFn)) return writeFn;

                var genericType = typeof(CsvSerializer<>).MakeGenericType(type);
                var mi = genericType.GetStaticMethod("WriteFn");
                var writeFactoryFn = (Func<WriteObjectDelegate>)mi.MakeDelegate(
                    typeof(Func<WriteObjectDelegate>));

                writeFn = writeFactoryFn();

                Dictionary<Type, WriteObjectDelegate> snapshot, newCache;
                do
                {
                    snapshot = WriteFnCache;
                    newCache = new Dictionary<Type, WriteObjectDelegate>(WriteFnCache);
                    newCache[type] = writeFn;

                } while (!ReferenceEquals(
                    Interlocked.CompareExchange(ref WriteFnCache, newCache, snapshot), snapshot));

                return writeFn;
            }
            catch (Exception ex)
            {
                Tracer.Instance.WriteError(ex);
                throw;
            }
        }

        private static Dictionary<Type, ParseStringDelegate> ReadFnCache = new Dictionary<Type, ParseStringDelegate>();
        internal static ParseStringDelegate GetReadFn(Type type)
        {
            try
            {
                ParseStringDelegate writeFn;
                if (ReadFnCache.TryGetValue(type, out writeFn)) return writeFn;

                var genericType = typeof(CsvSerializer<>).MakeGenericType(type);
                var mi = genericType.GetStaticMethod("ReadFn");
                var writeFactoryFn = (Func<ParseStringDelegate>)mi.MakeDelegate(
                    typeof(Func<ParseStringDelegate>));

                writeFn = writeFactoryFn();

                Dictionary<Type, ParseStringDelegate> snapshot, newCache;
                do
                {
                    snapshot = ReadFnCache;
                    newCache = new Dictionary<Type, ParseStringDelegate>(ReadFnCache);
                    newCache[type] = writeFn;

                } while (!ReferenceEquals(
                    Interlocked.CompareExchange(ref ReadFnCache, newCache, snapshot), snapshot));

                return writeFn;
            }
            catch (Exception ex)
            {
                Tracer.Instance.WriteError(ex);
                throw;
            }
        }

        public static string SerializeToCsv<T>(IEnumerable<T> records)
        {
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
            {
                writer.WriteCsv(records);
                return sb.ToString();
            }
        }

        public static string SerializeToString<T>(T value)
        {
            if (value == null) return null;
            if (typeof(T) == typeof(string)) return value as string;

            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
            {
                CsvSerializer<T>.WriteObject(writer, value);
            }
            return sb.ToString();
        }

        public static void SerializeToWriter<T>(T value, TextWriter writer)
        {
            if (value == null) return;
            if (typeof(T) == typeof(string))
            {
                writer.Write(value);
                return;
            }
            CsvSerializer<T>.WriteObject(writer, value);
        }

        public static void SerializeToStream<T>(T value, Stream stream)
        {
            if (value == null) return;
            var writer = new StreamWriter(stream, UTF8Encoding);
            CsvSerializer<T>.WriteObject(writer, value);
            writer.Flush();
        }

        public static void SerializeToStream(object obj, Stream stream)
        {
            if (obj == null) return;
            var writer = new StreamWriter(stream, UTF8Encoding);
            var writeFn = GetWriteFn(obj.GetType());
            writeFn(writer, obj);
            writer.Flush();
        }

        public static T DeserializeFromStream<T>(Stream stream)
        {
            throw new NotImplementedException();
        }

        public static object DeserializeFromStream(Type type, Stream stream)
        {
            if (stream == null) return null;
            var reader = new StreamReader(stream, UTF8Encoding);
            var readFn = GetReadFn(type);
            return readFn(reader.ReadToEnd());
        }

        public static T DeserializeFromString<T>(string text)
        {
            if (string.IsNullOrEmpty(text)) return default(T);
            return (T)CsvSerializer<T>.ReadObject(text);
        }

        public static object DeserializeFromString(Type type, string text)
        {
            throw new NotImplementedException();
        }

        public static void WriteLateBoundObject(TextWriter writer, object value)
        {
            if (value == null) return;
            var writeFn = GetWriteFn(value.GetType());
            writeFn(writer, value);
        }

        public static object ReadLateBoundObject(Type type, string value)
        {
            if (value == null) return null;
            var readFn = GetReadFn(type);
            return readFn(value);
        }
    }

    public static class CsvSerializer<T>
    {
        private static readonly WriteObjectDelegate WriteCacheFn;
        private static readonly ParseStringDelegate ReadCacheFn;

        public static WriteObjectDelegate WriteFn()
        {
            return WriteCacheFn;
        }

        private const string IgnoreResponseStatus = "ResponseStatus";

        private static Func<object, object> valueGetter = null;
        private static WriteObjectDelegate writeElementFn = null;

        private static WriteObjectDelegate GetWriteFn()
        {
            PropertyInfo firstCandidate = null;
            Type bestCandidateEnumerableType = null;
            PropertyInfo bestCandidate = null;

            if (typeof(T).IsValueType())
            {
                return JsvWriter<T>.WriteObject;
            }

            //If type is an enumerable property itself write that
            bestCandidateEnumerableType = typeof(T).GetTypeWithGenericTypeDefinitionOf(typeof(IEnumerable<>));
            if (bestCandidateEnumerableType != null)
            {
                var elementType = bestCandidateEnumerableType.GenericTypeArguments()[0];
                writeElementFn = CreateWriteFn(elementType);

                return WriteEnumerableType;
            }

            //Look for best candidate property if DTO
            if (typeof(T).IsDto() || typeof(T).HasAttribute<CsvAttribute>())
            {
                var properties = TypeConfig<T>.Properties;
                foreach (var propertyInfo in properties)
                {
                    if (propertyInfo.Name == IgnoreResponseStatus) continue;

                    if (propertyInfo.PropertyType == typeof(string)
                        || propertyInfo.PropertyType.IsValueType()
                        || propertyInfo.PropertyType == typeof(byte[]))
                        continue;

                    if (firstCandidate == null)
                    {
                        firstCandidate = propertyInfo;
                    }

                    var enumProperty = propertyInfo.PropertyType
                        .GetTypeWithGenericTypeDefinitionOf(typeof(IEnumerable<>));

                    if (enumProperty != null)
                    {
                        bestCandidateEnumerableType = enumProperty;
                        bestCandidate = propertyInfo;
                        break;
                    }
                }
            }

            //If is not DTO or no candidates exist, write self
            var noCandidatesExist = bestCandidate == null && firstCandidate == null;
            if (noCandidatesExist)
            {
                return WriteSelf;
            }

            //If is DTO and has an enumerable property serialize that
            if (bestCandidateEnumerableType != null)
            {
                valueGetter = bestCandidate.GetValueGetter(typeof(T));
                var elementType = bestCandidateEnumerableType.GenericTypeArguments()[0];
                writeElementFn = CreateWriteFn(elementType);

                return WriteEnumerableProperty;
            }

            //If is DTO and has non-enumerable, reference type property serialize that
            valueGetter = firstCandidate.GetValueGetter(typeof(T));
            writeElementFn = CreateWriteRowFn(firstCandidate.PropertyType);

            return WriteNonEnumerableType;
        }

        private static WriteObjectDelegate CreateWriteFn(Type elementType)
        {
            return CreateCsvWriterFn(elementType, "WriteObject");
        }

        private static WriteObjectDelegate CreateWriteRowFn(Type elementType)
        {
            return CreateCsvWriterFn(elementType, "WriteObjectRow");
        }

        private static WriteObjectDelegate CreateCsvWriterFn(Type elementType, string methodName)
        {
            var genericType = typeof(CsvWriter<>).MakeGenericType(elementType);
            var mi = genericType.GetStaticMethod(methodName);
            var writeFn = (WriteObjectDelegate)mi.MakeDelegate(typeof(WriteObjectDelegate));
            return writeFn;
        }

        public static void WriteEnumerableType(TextWriter writer, object obj)
        {
            writeElementFn(writer, obj);
        }

        public static void WriteSelf(TextWriter writer, object obj)
        {
            CsvWriter<T>.WriteRow(writer, (T)obj);
        }

        public static void WriteEnumerableProperty(TextWriter writer, object obj)
        {
            if (obj == null) return; //AOT

            var enumerableProperty = valueGetter(obj);
            writeElementFn(writer, enumerableProperty);
        }

        public static void WriteNonEnumerableType(TextWriter writer, object obj)
        {
            var nonEnumerableType = valueGetter(obj);
            writeElementFn(writer, nonEnumerableType);
        }

        public static void WriteObject(TextWriter writer, object value)
        {
            var hold = JsState.IsCsv;
            JsState.IsCsv = true;
            try
            {
                WriteCacheFn(writer, value);
            }
            finally
            {
                JsState.IsCsv = hold;
            }
        }


        static CsvSerializer()
        {
            if (typeof(T) == typeof(object))
            {
                WriteCacheFn = CsvSerializer.WriteLateBoundObject;
                ReadCacheFn = str => CsvSerializer.ReadLateBoundObject(typeof(T), str);
            }
            else
            {
                WriteCacheFn = GetWriteFn();
                ReadCacheFn = GetReadFn();
            }
        }


        public static ParseStringDelegate ReadFn()
        {
            return ReadCacheFn;
        }

        private static Action<object, object> valueSetter = null;
        private static ParseStringDelegate readElementFn = null;

        private static ParseStringDelegate GetReadFn()
        {
            PropertyInfo firstCandidate = null;
            Type bestCandidateEnumerableType = null;
            PropertyInfo bestCandidate = null;

            if (typeof(T).IsValueType())
            {
                return JsvReader<T>.Parse;
            }

            //If type is an enumerable property itself write that
            bestCandidateEnumerableType = typeof(T).GetTypeWithGenericTypeDefinitionOf(typeof(IEnumerable<>));
            if (bestCandidateEnumerableType != null)
            {
                var elementType = bestCandidateEnumerableType.GenericTypeArguments()[0];
                readElementFn = CreateReadFn(elementType);

                return ReadEnumerableType;
            }

            //Look for best candidate property if DTO
            if (typeof(T).IsDto() || typeof(T).HasAttribute<CsvAttribute>())
            {
                var properties = TypeConfig<T>.Properties;
                foreach (var propertyInfo in properties)
                {
                    if (propertyInfo.Name == IgnoreResponseStatus) continue;

                    if (propertyInfo.PropertyType == typeof(string)
                        || propertyInfo.PropertyType.IsValueType()
                        || propertyInfo.PropertyType == typeof(byte[]))
                        continue;

                    if (firstCandidate == null)
                    {
                        firstCandidate = propertyInfo;
                    }

                    var enumProperty = propertyInfo.PropertyType
                        .GetTypeWithGenericTypeDefinitionOf(typeof(IEnumerable<>));

                    if (enumProperty != null)
                    {
                        bestCandidateEnumerableType = enumProperty;
                        bestCandidate = propertyInfo;
                        break;
                    }
                }
            }

            //If is not DTO or no candidates exist, write self
            var noCandidatesExist = bestCandidate == null && firstCandidate == null;
            if (noCandidatesExist)
            {
                return ReadSelf;
            }

            //If is DTO and has an enumerable property serialize that
            if (bestCandidateEnumerableType != null)
            {
                valueSetter = bestCandidate.GetValueSetter(typeof(T));
                var elementType = bestCandidateEnumerableType.GenericTypeArguments()[0];
                readElementFn = CreateReadFn(elementType);

                return ReadEnumerableProperty;
            }

            //If is DTO and has non-enumerable, reference type property serialize that
            valueSetter = firstCandidate.GetValueSetter(typeof(T));
            readElementFn = CreateReadRowFn(firstCandidate.PropertyType);

            return ReadNonEnumerableType;
        }

        private static ParseStringDelegate CreateReadFn(Type elementType)
        {
            return CreateCsvReadFn(elementType, "ReadObject");
        }

        private static ParseStringDelegate CreateReadRowFn(Type elementType)
        {
            return CreateCsvReadFn(elementType, "ReadObjectRow");
        }

        private static ParseStringDelegate CreateCsvReadFn(Type elementType, string methodName)
        {
            var genericType = typeof(CsvReader<>).MakeGenericType(elementType);
            var mi = genericType.GetStaticMethod(methodName);
            var readFn = (ParseStringDelegate)mi.MakeDelegate(typeof(ParseStringDelegate));
            return readFn;
        }

        public static object ReadEnumerableType(string value)
        {
            return readElementFn(value);
        }

        public static object ReadSelf(string value)
        {
            return CsvReader<T>.ReadRow(value);
        }

        public static object ReadEnumerableProperty(string row)
        {
            if (row == null) return null; //AOT

            var value = readElementFn(row);
            var to = typeof(T).CreateInstance();
            valueSetter(to, value);
            return to;
        }

        public static object ReadNonEnumerableType(string row)
        {
            var value = readElementFn(row);
            var to = typeof(T).CreateInstance();
            valueSetter(to, value);
            return to;
        }

        public static object ReadObject(string value)
        {
            var hold = JsState.IsCsv;
            JsState.IsCsv = true;
            try
            {
                return ReadCacheFn(value);
            }
            catch (Exception ex)
            {
                throw;
            }
            finally
            {
                JsState.IsCsv = hold;
            }
        }

    }
}