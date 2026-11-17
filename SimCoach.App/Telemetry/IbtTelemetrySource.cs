using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SimCoach.Telemetry;

public sealed record IbtVariable(
    string Name,
    string Description,
    string Unit,
    int Type,
    int Offset,
    int Count);

public static class IbtTelemetrySource
{
    private const int HeaderSize = 112;
    private const int DiskSubHeaderSize = 32;
    private const int VariableHeaderSize = 144;

    public static IReadOnlyList<TelemetrySample> Load(string path) => LoadWithMetadata(path).Samples;

    public static TelemetryLoadResult LoadWithMetadata(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderSize + DiskSubHeaderSize)
        {
            throw new InvalidDataException("The IBT file is shorter than its header.");
        }

        var header = ReadHeader(bytes);
        ValidateHeader(header, bytes.Length);
        var variables = ReadVariables(bytes, header);
        var byName = variables.ToDictionary(variable => variable.Name, StringComparer.OrdinalIgnoreCase);
        var recordCount = ReadInt32(bytes, 140);
        var firstRecordOffset = ReadInt32(bytes, 52);
        var requiredEnd = checked((long)firstRecordOffset + (long)recordCount * header.BufferLength);
        if (requiredEnd > bytes.Length)
        {
            throw new InvalidDataException("The IBT file ends before all telemetry records are available.");
        }

        var samples = new List<TelemetrySample>(recordCount);
        var firstSessionSeconds = ReadRequiredDouble(bytes, firstRecordOffset, header.BufferLength, byName, "SessionTime");
        var previousLapNumber = 0;
        var previousLapDistanceKm = 0d;

        for (var index = 0; index < recordCount; index++)
        {
            var recordOffset = checked(firstRecordOffset + index * header.BufferLength);
            var lapNumber = ReadInt(byName, bytes, recordOffset, "Lap") ?? previousLapNumber;
            if (lapNumber != previousLapNumber)
            {
                previousLapDistanceKm = 0;
                previousLapNumber = lapNumber;
            }

            var lapDistanceKm = ReadFloat(byName, bytes, recordOffset, "LapDist") is { } lapDistance
                ? Math.Max(0, lapDistance / 1000d)
                : previousLapDistanceKm;
            previousLapDistanceKm = Math.Max(previousLapDistanceKm, lapDistanceKm);

            var progress = ReadFloat(byName, bytes, recordOffset, "LapDistPct");
            if (progress.HasValue && progress.Value > 1)
            {
                progress /= 100;
            }

            var sessionSeconds = ReadRequiredDouble(bytes, recordOffset, header.BufferLength, byName, "SessionTime");
            samples.Add(new TelemetrySample(
                TimeSpan.FromSeconds(Math.Max(0, sessionSeconds - firstSessionSeconds)),
                lapNumber,
                lapDistanceKm,
                ToKph(ReadFloat(byName, bytes, recordOffset, "Speed") ?? 0),
                NormalizeInput(ReadFloat(byName, bytes, recordOffset, "Throttle") ?? 0),
                NormalizeInput(ReadFloat(byName, bytes, recordOffset, "Brake") ?? 0),
                ReadFloat(byName, bytes, recordOffset, "SteeringWheelAngle") ?? 0,
                ReadInt(byName, bytes, recordOffset, "Gear") ?? 0,
                LapProgress: progress,
                Rpm: ReadFloat(byName, bytes, recordOffset, "RPM"),
                Clutch: NormalizeOptional(ReadFloat(byName, bytes, recordOffset, "Clutch")),
                LateralAcceleration: ReadFloat(byName, bytes, recordOffset, "LatAccel"),
                LongitudinalAcceleration: ReadFloat(byName, bytes, recordOffset, "LongAccel"),
                VerticalAcceleration: ReadFloat(byName, bytes, recordOffset, "VertAccel"),
                YawRate: ReadFloat(byName, bytes, recordOffset, "YawRate")));
        }

        var metadata = new TelemetrySessionMetadata(
            TelemetrySourceKind.IracingIbt,
            path,
            TelemetryChannelAvailability.FromHeaders(variables.Select(variable => variable.Name)),
            samples.Count,
            samples.Select(sample => sample.LapNumber).ToHashSet());
        return new TelemetryLoadResult(samples, metadata);
    }

    public static IReadOnlyList<IbtVariable> ReadVariables(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var header = ReadHeader(bytes);
        ValidateHeader(header, bytes.Length);
        return ReadVariables(bytes, header);
    }

    private static IbtHeader ReadHeader(byte[] bytes) => new(
        ReadInt32(bytes, 0),
        ReadInt32(bytes, 8),
        ReadInt32(bytes, 16),
        ReadInt32(bytes, 20),
        ReadInt32(bytes, 24),
        ReadInt32(bytes, 28),
        ReadInt32(bytes, 36),
        ReadInt32(bytes, 52));

    private static IReadOnlyList<IbtVariable> ReadVariables(byte[] bytes, IbtHeader header)
    {
        var variables = new List<IbtVariable>(header.VariableCount);
        for (var index = 0; index < header.VariableCount; index++)
        {
            var offset = checked(header.VariableHeaderOffset + index * VariableHeaderSize);
            variables.Add(new IbtVariable(
                ReadString(bytes, offset + 16, 32),
                ReadString(bytes, offset + 48, 64),
                ReadString(bytes, offset + 112, 32),
                ReadInt32(bytes, offset),
                ReadInt32(bytes, offset + 4),
                ReadInt32(bytes, offset + 8)));
        }

        return variables;
    }

    private static void ValidateHeader(IbtHeader header, int fileLength)
    {
        if (header.Version is < 1 or > 2 || header.TickRate <= 0 || header.VariableCount <= 0 || header.BufferLength <= 0)
        {
            throw new InvalidDataException("The IBT header is not valid.");
        }

        var variableEnd = checked((long)header.VariableHeaderOffset + (long)header.VariableCount * VariableHeaderSize);
        if (header.VariableHeaderOffset < HeaderSize + DiskSubHeaderSize || variableEnd > fileLength)
        {
            throw new InvalidDataException("The IBT variable-header region is not valid.");
        }

        if (header.FirstRecordOffset < variableEnd || header.FirstRecordOffset >= fileLength)
        {
            throw new InvalidDataException("The IBT telemetry-record region is not valid.");
        }
    }

    private static double ReadRequiredDouble(byte[] bytes, int recordOffset, int bufferLength, IReadOnlyDictionary<string, IbtVariable> variables, string name)
    {
        if (!variables.TryGetValue(name, out var variable))
        {
            throw new InvalidDataException($"The IBT file does not contain the required {name} channel.");
        }

        return ReadValue(bytes, recordOffset, bufferLength, variable) switch
        {
            double value => value,
            float value => value,
            _ => throw new InvalidDataException($"The IBT {name} channel has an unsupported type.")
        };
    }

    private static float? ReadFloat(IReadOnlyDictionary<string, IbtVariable> variables, byte[] bytes, int recordOffset, string name) =>
        variables.TryGetValue(name, out var variable) ? Convert.ToSingle(ReadValue(bytes, recordOffset, 0, variable)) : null;

    private static int? ReadInt(IReadOnlyDictionary<string, IbtVariable> variables, byte[] bytes, int recordOffset, string name) =>
        variables.TryGetValue(name, out var variable) ? Convert.ToInt32(ReadValue(bytes, recordOffset, 0, variable)) : null;

    private static object ReadValue(byte[] bytes, int recordOffset, int bufferLength, IbtVariable variable)
    {
        var valueOffset = checked(recordOffset + variable.Offset);
        if (valueOffset < recordOffset || (bufferLength > 0 && variable.Offset >= bufferLength))
        {
            throw new InvalidDataException($"The IBT channel {variable.Name} points outside its telemetry row.");
        }

        return variable.Type switch
        {
            1 => bytes[valueOffset] != 0,
            2 or 3 => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueOffset, 4)),
            4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueOffset, 4))),
            5 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(valueOffset, 8))),
            _ => throw new InvalidDataException($"The IBT channel {variable.Name} has unsupported type {variable.Type}.")
        };
    }

    private static string ReadString(byte[] bytes, int offset, int length)
    {
        var value = Encoding.ASCII.GetString(bytes, offset, length);
        var terminator = value.IndexOf('\0');
        return (terminator >= 0 ? value[..terminator] : value).Trim();
    }

    private static int ReadInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static double ToKph(double metersPerSecond) => Math.Max(0, metersPerSecond * 3.6);

    private static double NormalizeInput(double value) => Math.Clamp(Math.Abs(value) > 1.1 ? value / 100 : value, 0, 1);

    private static double? NormalizeOptional(float? value) => value.HasValue ? NormalizeInput(value.Value) : null;

    private sealed record IbtHeader(
        int Version,
        int TickRate,
        int SessionInfoLength,
        int SessionInfoOffset,
        int VariableCount,
        int VariableHeaderOffset,
        int BufferLength,
        int FirstRecordOffset);
}
