using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

namespace Memory.Storage.Internal;

internal sealed class FloatArrayVectorConverter()
    : ValueConverter<float[], Vector>(arr => new Vector(arr), v => v.ToArray());
