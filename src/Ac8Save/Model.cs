using System.Text;

namespace Ac8Save;

// UE 5.4 FPropertyTypeName: a name with nested type parameters.
public sealed class TypeName
{
    public string Name = "";
    public List<TypeName> Inner = new();
    public override string ToString() => Inner.Count == 0 ? Name : $"{Name}<{string.Join(",", Inner)}>";
    public TypeName Clone() => new() { Name = Name, Inner = Inner.Select(i => i.Clone()).ToList() };
}

[Flags]
public enum TagFlags : byte
{
    None = 0,
    HasArrayIndex = 0x01,
    HasPropertyGuid = 0x02,
    HasPropertyExtensions = 0x04,
    HasBinaryOrNativeSerialize = 0x08,
    BoolTrue = 0x10,
    SkippedSerialize = 0x20,
}

public abstract class Node
{
    public Node? Parent;
    public abstract IEnumerable<Node> Children { get; }
    public abstract string Label { get; }
}

// One tagged property: Name, TypeName, Size, Flags, [ArrayIndex], [Guid], payload.
public sealed class Property : Node
{
    public string Name = "";
    public TypeName Type = new();
    public TagFlags Flags;
    public int ArrayIndex;
    public byte[]? Guid;
    public Value Value = null!;

    public override IEnumerable<Node> Children => Value.Children;
    public override string Label => $"{Name} : {Type}";
}

public abstract class Value : Node
{
    public override IEnumerable<Node> Children => Enumerable.Empty<Node>();
    public override string Label => ToString() ?? "";
}

public sealed class IntValue : Value { public long V; public string Kind = "IntProperty"; public override string ToString() => V.ToString(); }
public sealed class FloatValue : Value { public double V; public bool IsDouble; public override string ToString() => V.ToString("R"); }
public sealed class BoolValue : Value { public bool V; public override string ToString() => V.ToString(); }
public sealed class StrValue : Value { public string V = ""; public bool Utf16; public override string ToString() => V; }
public sealed class EnumValue : Value { public string V = ""; public override string ToString() => V; }
// ByteProperty with size 1 is a raw byte (used for enum-backed bytes like DifficultyLevel).
public sealed class ByteValue : Value { public byte V; public override string ToString() => V.ToString(); }
public sealed class RawValue : Value { public byte[] Data = Array.Empty<byte>(); public override string ToString() => $"{Data.Length} bytes"; }

public sealed class StructValue : Value
{
    public List<Property> Props = new();
    public override IEnumerable<Node> Children => Props;
    public override string ToString() => $"struct ({Props.Count})";
}

// Element of an array/set/map; Index is cosmetic.
public sealed class Element : Node
{
    public int Index;
    public Value Value = null!;
    public override IEnumerable<Node> Children => Value.Children;
    public override string Label => $"[{Index}]";
}

public sealed class ArrayValue : Value
{
    public TypeName ElemType = new();
    // UE 5.4 still writes a legacy inner FPropertyTag before struct array elements.
    public Property? LegacyInnerTag;
    public List<Element> Items = new();
    public override IEnumerable<Node> Children => Items;
    public override string ToString() => $"array ({Items.Count})";
}

public sealed class SetValue : Value
{
    public TypeName ElemType = new();
    public int RemovedCount;
    public List<Element> Items = new();
    public override IEnumerable<Node> Children => Items;
    public override string ToString() => $"set ({Items.Count})";
}

public sealed class MapEntry : Node
{
    public int Index;
    public Value Key = null!;
    public Value Val = null!;
    public override IEnumerable<Node> Children => new Node[] { Key, Val };
    public override string Label => $"[{Index}] {Key}";
}

public sealed class MapValue : Value
{
    public TypeName KeyType = new();
    public TypeName ValType = new();
    public int RemovedCount;
    public List<MapEntry> Items = new();
    public override IEnumerable<Node> Children => Items;
    public override string ToString() => $"map ({Items.Count})";
}

// Outer GVAS file: header bytes kept verbatim, then SavedVersion, PackedData, Checksum.
public sealed class SaveFile
{
    public byte[] HeaderBytes = Array.Empty<byte>();
    public string ClassName = "";
    public int SavedVersion;
    public uint StoredChecksum;
    // PackedData is a sequence of property blocks, each terminated by "None".
    public List<StructValue> Sections = new();
    public IEnumerable<Property> AllProps => Sections.SelectMany(s => s.Props);
}
