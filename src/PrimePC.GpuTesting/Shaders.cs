using ComputeSharp;

namespace PrimePC.GpuTesting;

/// <summary>
/// Carga de computação determinística: cadeias de multiplicação/soma em ponto flutuante e hashing inteiro.
/// Para a mesma entrada, uma GPU estável produz exatamente o mesmo resultado em toda execução.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct StressShader(ReadWriteBuffer<uint> output, int iterations, uint seed) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X;
        uint h = ((uint)i * 747796405u) + seed;
        float a = ((float)(h & 1023u) * 0.001f) + 1.0f;
        float b = 0.5f;
        float c = 0.25f;
        float d = 0.125f;
        for (int k = 0; k < iterations; k++)
        {
            a = (a * 0.999f) + (b * 0.001f);
            b = (b * 0.998f) + (c * 0.002f);
            c = (c * 0.997f) + (d * 0.003f);
            d = (d * 0.996f) + (a * 0.004f);
            h ^= h << 13;
            h ^= h >> 17;
            h ^= h << 5;
        }

        output[i] = h ^ Hlsl.AsUInt(a) ^ Hlsl.AsUInt(b) ^ Hlsl.AsUInt(c) ^ Hlsl.AsUInt(d);
    }
}

/// <summary>Conta, na própria GPU, quantos resultados diferem da referência.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct CompareShader(ReadWriteBuffer<uint> output, ReadWriteBuffer<uint> reference, ReadWriteBuffer<uint> errors) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X;
        if (output[i] != reference[i])
            Hlsl.InterlockedAdd(ref errors[0], 1u);
    }
}

/// <summary>
/// Grava um padrão num trecho da VRAM. Modos: 0 = constante; 1 = endereço próprio; 2 = endereço invertido;
/// 3 = pseudoaleatório (hash do endereço com a semente).
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct VramWriteShader(ReadWriteBuffer<uint> buffer, int offset, uint mode, uint value, uint baseIndex) : IComputeShader
{
    public void Execute()
    {
        int i = offset + ThreadIds.X;
        uint address = baseIndex + (uint)i;
        uint expected = value;
        if (mode == 1u)
        {
            expected = address;
        }
        else if (mode == 2u)
        {
            expected = ~address;
        }
        else if (mode == 3u)
        {
            uint x = (address * 2654435761u) ^ value;
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            expected = x;
        }

        buffer[i] = expected;
    }
}

/// <summary>Verifica um trecho da VRAM e registra até 64 divergências (endereço, esperado, lido).</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct VramVerifyShader(
    ReadWriteBuffer<uint> buffer,
    ReadWriteBuffer<int> errorCount,
    ReadWriteBuffer<uint> errorLog,
    int offset,
    uint mode,
    uint value,
    uint baseIndex) : IComputeShader
{
    public void Execute()
    {
        int i = offset + ThreadIds.X;
        uint address = baseIndex + (uint)i;
        uint expected = value;
        if (mode == 1u)
        {
            expected = address;
        }
        else if (mode == 2u)
        {
            expected = ~address;
        }
        else if (mode == 3u)
        {
            uint x = (address * 2654435761u) ^ value;
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            expected = x;
        }

        uint actual = buffer[i];
        if (actual != expected)
        {
            int slot;
            Hlsl.InterlockedAdd(ref errorCount[0], 1, out slot);
            if (slot < 64)
            {
                errorLog[slot * 3] = address;
                errorLog[(slot * 3) + 1] = expected;
                errorLog[(slot * 3) + 2] = actual;
            }
        }
    }
}
