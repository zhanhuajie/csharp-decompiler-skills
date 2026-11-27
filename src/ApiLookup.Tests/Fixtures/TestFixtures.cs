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

    /// <summary>Extension helpers for <see cref="SampleBaseEntity"/>.</summary>
    public static class SampleEntityExtensions
    {
        /// <summary>Creates an off-screen preview device snapshot for an entity.</summary>
        public static string CreateAutoCADOffScreenDeviceSnapshot(this SampleBaseEntity entity, int width = 800)
            => $"{entity.Handle}:{width}";
    }

    /// <summary>Factory producing pipe segments.</summary>
    public static class SamplePipeFactory
    {
        /// <summary>Builds a default straight pipe segment.</summary>
        public static SamplePipeSegment CreateDefaultPipe(double od) => new(od);

        public static IList<SamplePipeSegment> CreatePipeBatch(int count) => new List<SamplePipeSegment>();

        public static Task<SamplePipeSegment> CreatePipeAsync() => Task.FromResult(new SamplePipeSegment(10.0));

        public static SamplePipeSegment[,,] CreatePipeGrid() => new SamplePipeSegment[1, 1, 1];

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

    public interface ISampleItemSource
    {
        IEnumerable<ISampleItem> GetItems();
    }

    public class SampleItemImpl : ISampleItem, IDictionary<string, SamplePipeSegment>
    {
        private readonly Dictionary<string, SamplePipeSegment> _inner = new();
        public string ItemCode => "ITEM";
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
