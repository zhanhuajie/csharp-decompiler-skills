using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace ApiLookup.Tests.Fixtures
{
    /// <summary>Base CAD entity for hierarchy and inheritance tests.</summary>
    public abstract class SampleBaseEntity
    {
        /// <summary>Unique hexadecimal handle.</summary>
        public string Handle { get; set; } = string.Empty;

        /// <summary>Moves the entity by an offset.</summary>
        /// <param name="dx">Delta X in millimeters.</param>
        /// <param name="dy">Delta Y in millimeters.</param>
        public virtual void Translate(double dx, double dy = 0.0) { }
    }

    /// <summary>Represents a 3D pipe segment with <see cref="SampleBaseEntity"/> identity.</summary>
    /// <remarks>Used to verify XML doc extraction, generics, and extension lookup.</remarks>
    public sealed class SamplePipeSegment : SampleBaseEntity, IDisposable
    {
        /// <summary>Creates a new pipe segment.</summary>
        public SamplePipeSegment(double outerDiameter, SamplePipeKind kind = SamplePipeKind.Straight)
        {
            OuterDiameter = outerDiameter;
            Kind = kind;
        }

        /// <summary>Outer diameter in mm.</summary>
        public double OuterDiameter { get; }

        /// <summary>Pipe classification.</summary>
        public SamplePipeKind Kind { get; }

        /// <summary>Tries to resolve connected port coordinates.</summary>
        /// <param name="portName">Port name such as S1.</param>
        /// <param name="coords">Output coordinates array.</param>
        /// <param name="tags">Optional filter tags.</param>
        /// <returns>True when the port exists.</returns>
        public bool TryGetPort(string portName, out double[] coords, params string[] tags)
        {
            coords = Array.Empty<double>();
            return false;
        }

        /// <summary>Converts a pipe segment to its handle string.</summary>
        public static implicit operator string(SamplePipeSegment seg) => seg.Handle;

        /// <summary>Nested diagnostic info.</summary>
        public struct PortSnapshot
        {
            public string Name;
        }

        public void Dispose() { }
    }

    /// <summary>Classification of sample pipe segments.</summary>
    public enum SamplePipeKind
    {
        /// <summary>Straight run.</summary>
        Straight = 0,
        /// <summary>Bent elbow run.</summary>
        Elbow = 2,
    }

    [Flags]
    public enum SampleAccessFlags
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4,
    }

    public readonly struct SampleReadOnlyPoint
    {
        private static readonly double OriginX = 0.0;
        public SampleReadOnlyPoint(in double x) { }
        public ref readonly double X => ref OriginX;
    }

    public ref struct SampleRefSpanReader
    {
        private readonly int[] _buf;
        public SampleRefSpanReader(int[] buf) => _buf = buf;
        public ref int Position => ref _buf[0];
    }

    public readonly ref struct SampleReadOnlyRefView
    {
    }

    public static class OuterBox
    {
        public static class MiddleBox
        {
            public sealed class DeepLeaf
            {
            }
        }
    }

    /// <summary>Extension helpers for <see cref="SampleBaseEntity"/>.</summary>
    public static class SampleEntityExtensions
    {
        /// <summary>Creates an off-screen preview device snapshot for an entity.</summary>
        public static string CreateAutoCADOffScreenDeviceSnapshot(this SampleBaseEntity entity, int width = 800)
            => $"{entity.Handle}:{width}";

        /// <summary>Generic constrained extension helper targeting any SampleBaseEntity.</summary>
        public static T UpgradeEntityOpen<T>(this T entity) where T : SampleBaseEntity
            => entity;

        /// <summary>Array-only extension that must NOT appear on a single SamplePipeSegment.</summary>
        public static int CountSegmentArray(this SamplePipeSegment[] segments)
            => segments.Length;

        /// <summary>Extension requiring both SampleBaseEntity and ISampleItem.</summary>
        public static T UpgradeTaggedEntity<T>(this T entity) where T : SampleBaseEntity, ISampleItem
            => entity;

        /// <summary>Extension requiring both SampleBaseEntity and IDisposable.</summary>
        public static T UpgradeDisposableEntity<T>(this T entity) where T : SampleBaseEntity, IDisposable
            => entity;

        /// <summary>Struct-only extension requiring ISampleItem.</summary>
        public static string FormatTaggedStruct<T>(this T item) where T : struct, ISampleItem
            => item.ItemCode;

        /// <summary>Extension targeting a nested type inside a constructed generic host.</summary>
        public static string DescribeInnerToken(this SampleGenericHost<string>.InnerToken token)
            => nameof(SampleGenericHost<string>.InnerToken);
    }

    public class SampleStringList : List<string>
    {
    }

    public class SampleGenericHost<T>
    {
        public sealed class InnerToken
        {
        }

        public sealed class OtherToken
        {
        }

        public sealed class NestedPair<U>
        {
        }
    }

    public sealed class SampleNestedDocConsumer
    {
        /// <summary>Consumes an inner token.</summary>
        public void Consume(SampleGenericHost<string>.InnerToken token) { }

        /// <summary>Consumes another token.</summary>
        public void Consume(SampleGenericHost<string>.OtherToken token) { }

        /// <summary>Consumes a nested generic pair.</summary>
        public void Consume(SampleGenericHost<string>.NestedPair<int> pair) { }
    }

    public class SampleTaggedPipe : SampleBaseEntity, ISampleItem
    {
        public string ItemCode => "PIPE";
    }

    public readonly struct SampleTaggedStruct : ISampleItem
    {
        public string ItemCode => "STRUCT";
    }

    /// <summary>Factory producing pipe segments.</summary>
    public static class SamplePipeFactory
    {
        public const decimal DefaultWallThickness = 12.75m;

        /// <summary>Builds a default straight pipe segment.</summary>
        public static SamplePipeSegment CreateDefaultPipe(double od, decimal tolerance = 0.05m) => new(od);

        public static IList<SamplePipeSegment> CreatePipeBatch(int count) => new List<SamplePipeSegment>();

        public static Task<SamplePipeSegment> CreatePipeAsync() => Task.FromResult(new SamplePipeSegment(10.0));

        public static SamplePipeSegment[,,] CreatePipeGrid() => new SamplePipeSegment[1, 1, 1];

        public static SampleAccessFlags OpenWithFlags(
            SampleAccessFlags flags = SampleAccessFlags.Read | SampleAccessFlags.Write,
            SamplePipeKind fallbackKind = (SamplePipeKind)99) => flags;

        public static int op_Custom_Add(int x, int y) => x + y;
    }

    /// <summary>Generic constrained container.</summary>
    public class SampleContainer<T> where T : SampleBaseEntity, IDisposable
    {
        public const string EscapedText = "line1\n\"quoted\"\\end";

        public T? Current { get; init; }

        public T? this[int index] => Current;

        public event EventHandler? Changed;

        public virtual string Describe() => nameof(SampleContainer<T>);

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Generic type verifying class/base/new() constraints.</summary>
    public class SampleFactoryBox<T> where T : SampleBaseEntity, new()
    {
        public T Create() => new();
    }

    /// <summary>Concrete container deriving from <see cref="SampleContainer{T}"/>.</summary>
    public class SamplePipeContainer : SampleContainer<SamplePipeSegment>
    {
        /// <summary>Gets or sets the active pipe segment.</summary>
        /// <value>Active pipe segment instance or null.</value>
        public SamplePipeSegment? Active { get; set; }

        public sealed override string Describe() => "pipe";

        public void ProcessItem<U>(U item) where U : struct { }

        public SamplePipeKind NormalizeKind(SamplePipeKind kind = SamplePipeKind.Elbow) => kind;
    }

    public interface ISampleItem
    {
        string ItemCode { get; }
    }

    public interface IDerivedSampleItem : ISampleItem
    {
        string SubCategory { get; }
    }

    public interface ISampleItemSource
    {
        IEnumerable<ISampleItem> GetItems();
    }

    public class SampleItemImpl : IDerivedSampleItem, IDictionary<string, SamplePipeSegment>
    {
        private readonly Dictionary<string, SamplePipeSegment> _inner = new();
        public string ItemCode => "ITEM";
        public string SubCategory => "SUB";
        public SamplePipeSegment this[string key] { get => _inner[key]; set => _inner[key] = value; }
        public ICollection<string> Keys => _inner.Keys;
        public ICollection<SamplePipeSegment> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool IsReadOnly => false;
        public void Add(string key, SamplePipeSegment value) => _inner.Add(key, value);
        public void Add(KeyValuePair<string, SamplePipeSegment> item) => ((ICollection<KeyValuePair<string, SamplePipeSegment>>)_inner).Add(item);
        public void Clear() => _inner.Clear();
        public bool Contains(KeyValuePair<string, SamplePipeSegment> item) => ((ICollection<KeyValuePair<string, SamplePipeSegment>>)_inner).Contains(item);
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<string, SamplePipeSegment>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, SamplePipeSegment>>)_inner).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<string, SamplePipeSegment>> GetEnumerator() => _inner.GetEnumerator();
        public bool Remove(string key) => _inner.Remove(key);
        public bool Remove(KeyValuePair<string, SamplePipeSegment> item) => ((ICollection<KeyValuePair<string, SamplePipeSegment>>)_inner).Remove(item);
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out SamplePipeSegment value) => _inner.TryGetValue(key, out value);
        IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();
    }

    public sealed class DerivedSampleItemImpl : SampleItemImpl
    {
    }

    public static class FixtureSampleItemExtensions
    {
        public static string GetFixtureTag(this ISampleItem item) => item.ItemCode;

        public static int CountPipeEntries(this IDictionary<string, SamplePipeSegment> map) => map.Count;
    }

    public delegate bool SampleFilterHandler(SampleBaseEntity entity, int depth);

    /// <summary>三维管道段处理器</summary>
    public static class ChinesePipeProcessor
    {
        /// <summary>计算管道中心线长度</summary>
        public static double ComputePipeLength(double raw) => raw;
    }

    /// <summary>Helper type with rankingtest keyword only in summary.</summary>
    public class SummaryKeywordHolder
    {
    }

    /// <summary>Primary type whose name matches rankingtest.</summary>
    public class Rankingtest
    {
    }

    internal sealed class InternalFixtureHelper
    {
        public int SecretCode { get; set; }
    }

    /// <summary>Nullable-annotation fixtures exercising nested positions and cross-assembly reference types.</summary>
    public static class SampleNullability
    {
        public static List<string?>? GetNames(Dictionary<string, object?> map, string?[] items, string[]? maybeItems) => null;

        // Every position annotated: the compiler emits only NullableContext(2) on the method and no per-parameter attributes.
        public static System.Text.StringBuilder? Build(System.Text.StringBuilder? a, System.Text.StringBuilder? b) => a;

        public static KeyValuePair<string?, int> Pair((string? Name, int Count) tuple) => default;

        public static T? FirstOrNull<T>(IEnumerable<T?> items) where T : class => null;

        public static T Pick<T>(T value, T? fallback) where T : struct => value;
    }

    /// <summary>Non-generic arity twin.</summary>
    public class SampleArity
    {
    }

    /// <summary>Generic arity twin sharing the same bare name.</summary>
    public class SampleArity<T>
    {
    }

    public class DerivedFromArity : SampleArity
    {
    }

    public class DerivedFromGenericArity : SampleArity<int>
    {
    }

    /// <summary>Documented valve contract.</summary>
    public interface ISampleValve
    {
        /// <summary>Opens the valve to the requested percentage.</summary>
        /// <param name="percent">Opening ratio from 0 to 100.</param>
        void Open(int percent);
    }

    /// <summary>Base valve with documented flow computation.</summary>
    public abstract class SampleValveBase
    {
        /// <summary>Computes the volumetric flow rate through the valve.</summary>
        /// <returns>Flow in cubic meters per hour.</returns>
        public virtual double ComputeFlow(double pressure) => pressure;
    }

    /// <inheritdoc/>
    public sealed class SampleGateValve : SampleValveBase, ISampleValve
    {
        /// <inheritdoc/>
        public override double ComputeFlow(double pressure) => pressure * 2;

        /// <inheritdoc/>
        public void Open(int percent) { }

        /// <inheritdoc cref="SampleBaseEntity.Translate(double, double)"/>
        public void Shift(double dx, double dy) { }
    }
}

namespace ApiLookup.Tests.OtherNs
{
    public interface ISampleItem
    {
        int OtherId { get; }
    }

    public static class OtherNsSampleItemExtensions
    {
        public static int GetOtherTag(this ISampleItem item) => item.OtherId;
    }
}

namespace ApiLookup.Tests.FixturesExtra
{
    public sealed class ExtraFixtureType
    {
        public int ExtraValue { get; set; }
    }
}
