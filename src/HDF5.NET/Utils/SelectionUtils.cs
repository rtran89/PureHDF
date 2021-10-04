using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HDF5.NET
{
    internal record CopyInfo(
        ulong[] SourceDims,
        ulong[] SourceChunkDims,
        ulong[] TargetDims,
        ulong[] TargetChunkDims,
        HyperslabSelection SourceSelection,
        HyperslabSelection TargetSelection,
        Func<ulong[], Memory<byte>>? GetSourceBuffer,
        Func<ulong[], Stream>? GetSourceStream,
        Func<ulong[], Memory<byte>> GetTargetBuffer,
        int TypeSize
    );

    internal struct Step
    {
        public ulong[] Chunk { get; init; }

        public ulong Offset { get; init; }

        public ulong Length { get; init; }
    }

    internal static class SelectionUtils
    {
        public static IEnumerable<Step> Walk(int rank, ulong[] dims, ulong[] chunkDims, Selection selection)
        {
            /* check if there is anythng to do */
            if (selection.ElementCount == 0)
                yield break;

            /* validate rank */
            if (dims.Length != rank || chunkDims.Length != rank)
                throw new RankException($"The length of each array parameter must match the rank parameter.");

            /* prepare some useful arrays */
            var lastDim = rank - 1;
            var chunkLength = chunkDims.Aggregate(1UL, (x, y) => x * y);

            /* prepare last dimension variables */
            var lastChunkDim = chunkDims[lastDim];

            foreach (var slice in selection)
            {
                var remaining = slice.Length;

                while (remaining > 0)
                {
                    var scaledOffsets = new ulong[rank];
                    var chunkOffsets = new ulong[rank];

                    for (int dimension = 0; dimension < rank; dimension++)
                    {
                        scaledOffsets[dimension] = slice.Coordinates[dimension] / chunkDims[dimension];
                        chunkOffsets[dimension] = slice.Coordinates[dimension] % chunkDims[dimension];
                    }

                    var offset = chunkOffsets.ToLinearIndex(chunkDims);
                    var currentLength = Math.Min(lastChunkDim - chunkOffsets[lastDim], remaining);

                    yield return new Step()
                    {
                        Chunk = scaledOffsets,
                        Offset = offset,
                        Length = currentLength
                    };

                    remaining -= currentLength;
                    slice.Coordinates[lastDim] += currentLength;
                }
            }
        }

        public static void Copy(int sourceRank, int targetRank, CopyInfo copyInfo)
        {
            /* validate rank of selections */
            if (copyInfo.SourceSelection.Rank != sourceRank ||
                copyInfo.TargetSelection.Rank != targetRank)
                throw new RankException($"The length of each array parameter must match the rank parameter.");

            /* validate selections */
            if (copyInfo.SourceSelection.ElementCount != copyInfo.TargetSelection.ElementCount)
                throw new ArgumentException("The length of the source selection and target selection are not equal.");

            for (int dimension = 0; dimension < sourceRank; dimension++)
            {
                if (copyInfo.SourceSelection.GetStop(dimension) > copyInfo.SourceDims[dimension])
                    throw new ArgumentException("The source selection size exceeds the limits of the source buffer.");
            }

            for (int dimension = 0; dimension < targetRank; dimension++)
            {
                if (copyInfo.TargetSelection.GetStop(dimension) > copyInfo.TargetDims[dimension])
                    throw new ArgumentException("The target selection size exceeds the limits of the target buffer.");
            }

            /* validate rank of dims */
            if (copyInfo.SourceDims.Length != copyInfo.SourceSelection.Rank ||
                copyInfo.SourceChunkDims.Length != copyInfo.SourceSelection.Rank ||
                copyInfo.TargetDims.Length != copyInfo.TargetSelection.Rank ||
                copyInfo.TargetChunkDims.Length != copyInfo.TargetSelection.Rank)
                throw new RankException($"The length of each array parameter must match the rank parameter.");

            /* walkers */
            var sourceWalker = SelectionUtils
                .Walk(sourceRank, copyInfo.SourceDims, copyInfo.SourceChunkDims, copyInfo.SourceSelection)
                .GetEnumerator();

            var targetWalker = SelectionUtils
               .Walk(targetRank, copyInfo.TargetDims, copyInfo.TargetChunkDims, copyInfo.TargetSelection)
               .GetEnumerator();

            /* select method */
            if (copyInfo.GetSourceBuffer is not null)
                SelectionUtils.CopyMemory(sourceWalker, targetWalker, copyInfo);

            else if (copyInfo.GetSourceStream is not null)
                SelectionUtils.CopyStream(sourceWalker, targetWalker, copyInfo);

            else
                new Exception($"Either GetSourceBuffer() or GetSourceStream must be non-null.");
        }

        private static void CopyMemory(IEnumerator<Step> sourceWalker, IEnumerator<Step> targetWalker, CopyInfo copyInfo)
        {
            /* initialize source walker */
            var sourceBuffer = default(Memory<byte>);
            var lastSourceChunk = default(ulong[]);

            /* initialize target walker */
            var targetBuffer = default(Memory<byte>);
            var lastTargetChunk = default(ulong[]);
            var currentTarget = default(Memory<byte>);

            /* walk until end */
            while (sourceWalker.MoveNext())
            {
                /* load next source buffer */
                var sourceStep = sourceWalker.Current;

                if (sourceBuffer.Length == 0 /* if buffer not assigned yet */ ||
                    !sourceStep.Chunk.SequenceEqual(lastSourceChunk) /* or the chunk has changed */)
                {
                    sourceBuffer = copyInfo.GetSourceBuffer(sourceStep.Chunk);
                    lastSourceChunk = sourceStep.Chunk;
                }

                var currentSource = sourceBuffer.Slice(
                    (int)sourceStep.Offset * copyInfo.TypeSize,
                    (int)sourceStep.Length * copyInfo.TypeSize);

                while (currentSource.Length > 0)
                {
                    /* load next target buffer */
                    if (currentTarget.Length == 0)
                    {
                        var success = targetWalker.MoveNext();
                        var targetStep = targetWalker.Current;

                        if (!success || targetStep.Length == 0)
                            throw new UriFormatException("The target walker stopped early.");

                        if (targetBuffer.Length == 0 /* if buffer not assigned yet */ ||
                            !targetStep.Chunk.SequenceEqual(lastTargetChunk) /* or the chunk has changed */)
                        {
                            targetBuffer = copyInfo.GetTargetBuffer(targetStep.Chunk);
                            lastTargetChunk = targetStep.Chunk;
                        }

                        currentTarget = targetBuffer.Slice(
                            (int)targetStep.Offset * copyInfo.TypeSize,
                            (int)targetStep.Length * copyInfo.TypeSize);
                    }

                    /* copy */
                    var length = Math.Min(currentSource.Length, currentTarget.Length);

                    currentSource
                        .Slice(0, length)
                        .CopyTo(currentTarget);

                    currentSource = currentSource.Slice(length);
                    currentTarget = currentTarget.Slice(length);
                }
            }
        }

        private static void CopyStream(IEnumerator<Step> sourceWalker, IEnumerator<Step> targetWalker, CopyInfo copyInfo)
        {
            /* initialize source walker */
            var sourceStream = default(Stream);
            var lastSourceChunk = default(ulong[]);

            /* initialize target walker */
            var targetBuffer = default(Memory<byte>);
            var lastTargetChunk = default(ulong[]);
            var currentTarget = default(Memory<byte>);

            /* walk until end */
            while (sourceWalker.MoveNext())
            {
                /* load next source stream */
                var sourceStep = sourceWalker.Current;

                if (sourceStream is null /* if stream not assigned yet */ ||
                    !sourceStep.Chunk.SequenceEqual(lastSourceChunk) /* or the chunk has changed */)
                {
                    sourceStream = copyInfo.GetSourceStream(sourceStep.Chunk);
                    lastSourceChunk = sourceStep.Chunk;
                }

                sourceStream?.Seek((int)sourceStep.Offset * copyInfo.TypeSize, SeekOrigin.Begin);        // corresponds to 
                var currentLength = (int)sourceStep.Length * copyInfo.TypeSize;                          // sourceBuffer.Slice()

                while (currentLength > 0)
                {
                    /* load next target buffer */
                    if (currentTarget.Length == 0)
                    {
                        var success = targetWalker.MoveNext();
                        var targetStep = targetWalker.Current;

                        if (!success || targetStep.Length == 0)
                            throw new UriFormatException("The target walker stopped early.");

                        if (targetBuffer.Length == 0 /* if buffer not assigned yet */ ||
                            !targetStep.Chunk.SequenceEqual(lastTargetChunk) /* or the chunk has changed */)
                        {
                            targetBuffer = copyInfo.GetTargetBuffer(targetStep.Chunk);
                            lastTargetChunk = targetStep.Chunk;
                        }

                        currentTarget = targetBuffer.Slice(
                            (int)targetStep.Offset * copyInfo.TypeSize,
                            (int)targetStep.Length * copyInfo.TypeSize);
                    }

                    /* copy */
                    var length = Math.Min(currentLength, currentTarget.Length);
                    sourceStream?.Read(currentTarget.Slice(0, length).Span);                             // corresponds to span.CopyTo

                    sourceStream?.Seek((int)sourceStep.Offset * copyInfo.TypeSize, SeekOrigin.Begin);    // corresponds to 
                    currentLength -= (int)sourceStep.Length * copyInfo.TypeSize;                         // sourceBuffer.Slice()

                    currentTarget = currentTarget.Slice(length);
                }
            }
        }
    }
}
