using System.Text;

namespace Unvived;

/// <summary>A float32 tensor read from a TVM parameter file.</summary>
public sealed record Tensor(int[] Shape, float[] Data);

/// <summary>
/// Reader for TVM's saved parameter list (the <c>.params</c> files next to
/// SRanipal's compiled models): a name list followed by DLTensor records.
/// </summary>
public static class TvmParams
{
    private const ulong ListMagic = 0xF7E58D4F05049CB7;
    private const ulong ArrayMagic = 0xDD5E40F096B4A13F;
    private const byte FloatTypeCode = 2;

    public static Dictionary<string, Tensor> Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadUInt64() != ListMagic)
            throw new InvalidDataException($"{path}: not a TVM parameter file");
        reader.ReadUInt64(); // reserved

        var names = new string[checked((int)reader.ReadUInt64())];
        for (int i = 0; i < names.Length; i++)
            names[i] = Encoding.UTF8.GetString(reader.ReadBytes(checked((int)reader.ReadUInt64())));

        if ((int)reader.ReadUInt64() != names.Length)
            throw new InvalidDataException($"{path}: name and array counts differ");

        var tensors = new Dictionary<string, Tensor>(names.Length);
        foreach (string name in names)
            tensors[name] = ReadTensor(reader, path, name);
        return tensors;
    }

    private static Tensor ReadTensor(BinaryReader reader, string path, string name)
    {
        if (reader.ReadUInt64() != ArrayMagic)
            throw new InvalidDataException($"{path}: bad array header for {name}");
        reader.ReadUInt64(); // reserved
        reader.ReadInt32(); // device type
        reader.ReadInt32(); // device id
        int ndim = reader.ReadInt32();
        byte code = reader.ReadByte();
        byte bits = reader.ReadByte();
        ushort lanes = reader.ReadUInt16();
        if (code != FloatTypeCode || bits != 32 || lanes != 1)
            throw new InvalidDataException($"{path}: {name} is not float32");

        var shape = new int[ndim];
        long count = 1;
        for (int i = 0; i < ndim; i++)
        {
            shape[i] = checked((int)reader.ReadInt64());
            count *= shape[i];
        }
        long bytes = reader.ReadInt64();
        if (bytes != count * sizeof(float))
            throw new InvalidDataException($"{path}: {name} size does not match its shape");

        var data = new float[count];
        reader.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
        return new Tensor(shape, data);
    }
}
