using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Data;

// The definition what kind of page is used on Level2
#if !USE_BIG_FANOUT
using Level2 = Paprika.Store.DataPage;
#else
using Level2 = Paprika.Store.StorageFanOut.Level2Page;
#endif


namespace Paprika.Store;

/// <summary>
/// Components responsible for fanning out storage heavily. This means both, id mapping as well as the actual storage.
/// </summary>
public static class StorageFanOut
{
    public const int StorageConsumedNibbles = Level0.ConsumedNibbles + Level1Page.Level1ConsumedNibblesForStorage;
    public const string ScopeIds = "Ids";
    public const string ScopeStorage = "Storage";

    public enum Type
    {
        /// <summary>
        /// Represents the mapping of Keccak->int
        /// </summary>
        Id,

        /// <summary>
        /// Represents the actual storage mapped NibblePath ->int
        /// </summary>
        Storage
    }

    private const byte NibbleHalfLower = 0b0011;
    private const byte NibbleHalfHigher = 0b1100;
    private const byte NibbleHalfShift = NibblePath.NibbleShift / 2;
    private const byte TwoNibbleShift = NibblePath.NibbleShift * 2;

    /// <summary>
    /// Provides a convenient data structure for <see cref="RootPage"/>,
    /// to hold a list of child addresses of <see cref="DbAddressList.IDbAddressList"/> but with addition of
    /// handling the updates to addresses.
    /// </summary>
    public readonly ref struct Level0(ref DbAddressList.Of1024 addresses)
    {
        private readonly ref DbAddressList.Of1024 _addresses = ref addresses;

        public bool TryGet(IReadOnlyBatchContext batch, scoped in NibblePath key, Type type,
            out ReadOnlySpan<byte> result)
        {
            var index = GetIndex(key, out var sliced);

            var addr = _addresses[index];
            if (addr.IsNull)
            {
                result = default;
                return false;
            }

            return Level1Page.Wrap(batch.GetAt(addr))
                .TryGet(batch, sliced, type, out result);
        }

        public void Set(in NibblePath key, Type type, in ReadOnlySpan<byte> data, IBatchContext batch)
        {
            var index = GetIndex(key, out var sliced);
            var addr = _addresses[index];

            if (addr.IsNull)
            {
                var newPage = batch.GetNewPage(out addr, true);
                _addresses[index] = addr;

                newPage.Header.PageType = PageType.FanOutPage;
                newPage.Header.Level = ConsumedNibbles;

                Level1Page.Wrap(newPage).Set(sliced, type, data, batch);
                return;
            }

            // The page exists, update
            var updated = Level1Page.Wrap(batch.GetAt(addr)).Set(sliced, type, data, batch);
            _addresses[index] = batch.GetAddress(updated);
        }

        public void DeleteByPrefix(in NibblePath path, IBatchContext batch)
        {
            var index = GetIndex(path, out var sliced);
            var addr = _addresses[index];

            if (addr.IsNull)
            {
                return;
            }

            // The page exists, update
            var updated = Level1Page.Wrap(batch.GetAt(addr)).DeleteByPrefix(sliced, batch);
            _addresses[index] = batch.GetAddress(updated);
        }

        private static int GetIndex(scoped in NibblePath key, out NibblePath sliced)
        {
            Debug.Assert(key.IsOdd == false);

            // Consume 2 first nibbles as raw byte, shift and add lower half
            var at = (key.UnsafeSpan << NibbleHalfShift) + (key.GetAt(2) & NibbleHalfLower);

            Debug.Assert(0 <= at && at < DbAddressList.Of1024.Count);

            sliced = key.SliceFrom(ConsumedNibbles);
            return at;
        }

        public const int ConsumedNibbles = 2;

        public void Accept(IPageVisitor visitor, IPageResolver resolver)
        {
            using var scope = visitor.Scope(nameof(StorageFanOut));

            for (var i = 0; i < DbAddressList.Of1024.Count; i++)
            {
                var addr = _addresses[i];
                if (!addr.IsNull)
                {
                    Level1Page.Wrap(resolver.GetAt(addr)).Accept(i, visitor, resolver, addr);
                }
            }
        }
    }

    /// <summary>
    /// Represents a fan out for:
    /// - ids with <see cref="DbAddressList.Of4"/>
    /// - storage with <see cref="DbAddressList.Of1024"/>
    /// </summary>
    /// <param name="page"></param>
    [method: DebuggerStepThrough]
    private readonly unsafe struct Level1Page(Page page) : IPage
    {
        public static Level1Page Wrap(Page page) => Unsafe.As<Page, Level1Page>(ref page);

        private ref PageHeader Header => ref page.Header;

        private ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);

        public bool TryGet(IReadOnlyBatchContext batch, scoped in NibblePath key, Type type,
            out ReadOnlySpan<byte> result)
        {
            batch.AssertRead(Header);

            var index = GetIndex(key, type, out var sliced);

            var addr = type == Type.Id ? Data.Ids[index] : Data.Storage[index];

            if (addr.IsNull)
            {
                result = default;
                return false;
            }

            var p = batch.GetAt(addr);

            return type == Type.Id
                ? DataPage.Wrap(p).TryGet(batch, sliced, out result)
                : Level2.Wrap(p).TryGet(batch, sliced, out result);
        }

        public Page Set(in NibblePath key, Type type, in ReadOnlySpan<byte> data, IBatchContext batch)
        {
            if (Header.BatchId != batch.BatchId)
            {
                // the page is from another batch, meaning, it's readonly. Copy
                var writable = batch.GetWritableCopy(page);
                return new Level1Page(writable).Set(key, type, data, batch);
            }

            var index = GetIndex(key, type, out var sliced);

            if (type == Type.Id)
            {
                Set<DbAddressList.Of4, DataPage>(ref Data.Ids, index, sliced, data, batch, Level1ConsumedNibblesForIds);
            }
            else
            {
                Set<DbAddressList.Of1024, Level2>(ref Data.Storage, index, sliced, data, batch, Level1ConsumedNibblesForStorage);
            }

            return page;
        }

        public Page DeleteByPrefix(in NibblePath prefix, IBatchContext batch)
        {
            if (Header.BatchId != batch.BatchId)
            {
                // the page is from another batch, meaning, it's readonly.
                var writable = batch.GetWritableCopy(page);
                return new Level1Page(writable).DeleteByPrefix(prefix, batch);
            }

            var index = GetIndex(prefix, Type.Storage, out var sliced);

            var addr = Data.Storage[index];

            if (addr.IsNull)
            {
                return page;
            }

            // update after set
            addr = batch.GetAddress(Level2.Wrap(batch.GetAt(addr)).DeleteByPrefix(sliced, batch));
            Data.Storage[index] = addr;

            return page;
        }

        private void Set<TAddressList, TPage>(ref TAddressList list, int index, in NibblePath sliced,
            in ReadOnlySpan<byte> data,
            IBatchContext batch, int consumedNibbles)
            where TAddressList : struct, DbAddressList.IDbAddressList
            where TPage : struct, IPageWithData<TPage>
        {
            var addr = list[index];

            if (addr.IsNull)
            {
                // Clear manually
                var newPage = batch.GetNewPage(out addr, false);

                list[index] = addr;

                newPage.Header.PageType = PageType.DataPage;
                newPage.Header.Level = (byte)(Header.Level + consumedNibbles);

                var dataPage = TPage.Wrap(newPage);
                dataPage.Clear();
                dataPage.Set(sliced, data, batch);
                return;
            }

            // update after set
            addr = batch.GetAddress(TPage.Wrap(batch.GetAt(addr)).Set(sliced, data, batch));
            list[index] = addr;
        }

        private static int GetIndex(scoped in NibblePath key, Type type, out NibblePath sliced)
        {
            // Represents high part of the first nibble but lowered
            var hi = (key.Nibble0 & NibbleHalfHigher) >> NibbleHalfShift;

            Debug.Assert(0 <= hi && hi < 4);

            if (type == Type.Id)
            {
                sliced = key.SliceFrom(Level1ConsumedNibblesForIds);
                return hi;
            }

            var at = (hi << TwoNibbleShift) + // 0.5 nibble
                     (key.GetAt(1) << NibblePath.NibbleShift) + // 1 nibble 
                     key.GetAt(2); // 1 nibble
            Debug.Assert(0 <= at && at < DbAddressList.Of1024.Count);

            sliced = key.SliceFrom(Level1ConsumedNibblesForStorage);
            return at;
        }

        /// <summary>
        /// This is effectively 0.5 of the nibble as the 1.5 is consumed on the higher level.
        /// </summary>
        private const int Level1ConsumedNibblesForIds = 1;

        /// <summary>
        /// This is effectively 1.5 of the nibble as the 1.5 is consumed on the higher level.
        /// </summary>
        public const int Level1ConsumedNibblesForStorage = 3;

        public void Accept(int bucketOf1024, IPageVisitor visitor, IPageResolver resolver, DbAddress addr)
        {
            Debug.Assert(bucketOf1024 < DbAddressList.Of1024.Count,
                $"The buckets should be within the range of level 0 which uses {nameof(DbAddressList.Of1024)}");

            var builder = new NibblePath.Builder(stackalloc byte[NibblePath.Builder.DecentSize]);

            var nibble0 = (byte)((bucketOf1024 >> (NibblePath.NibbleShift + NibbleHalfShift)) & NibblePath.NibbleMask);
            var nibble1 = (byte)((bucketOf1024 >> NibbleHalfShift) & NibblePath.NibbleMask);
            var nibble2Low = (byte)(bucketOf1024 & NibbleHalfLower);

            builder.Push(nibble0, nibble1);
            {
                using var scope = visitor.On(ref builder, this, addr);

                using (visitor.Scope(ScopeIds))
                {
                    for (var i = 0; i < DbAddressList.Of4.Count; i++)
                    {
                        var bucket = Data.Ids[i];

                        if (!bucket.IsNull)
                        {
                            builder.Push((byte)((i << NibbleHalfShift) | nibble2Low));
                            {
                                DataPage.Wrap(resolver.GetAt(bucket)).Accept(ref builder, visitor, resolver, bucket);
                            }
                            builder.Pop();
                        }
                    }
                }

                using (visitor.Scope(ScopeStorage))
                {
                    for (var i = 0; i < DbAddressList.Of1024.Count; i++)
                    {
                        var bucket = Data.Storage[i];
                        if (!bucket.IsNull)
                        {
                            var nibbleStart = i >> (NibblePath.NibbleShift + NibbleHalfShift);

                            builder.Push((byte)((nibbleStart & NibbleHalfHigher) | nibble2Low));
                            builder.Push((byte)((i >> NibblePath.NibbleShift) & NibblePath.NibbleMask));
                            builder.Push((byte)((i >> NibbleHalfShift) & NibblePath.NibbleMask));

                            Level2.Wrap(resolver.GetAt(bucket)).Accept(ref builder, visitor, resolver, bucket);

                            builder.Pop(3);
                        }
                    }
                }
            }

            builder.Pop(2);

            builder.Dispose();
        }

        [StructLayout(LayoutKind.Explicit, Size = Size)]
        private struct Payload
        {
            private const int Size = Page.PageSize - PageHeader.Size;

            /// <summary>
            /// Ids are mapped using a single half-nibble
            /// </summary>
            [FieldOffset(0)] public DbAddressList.Of4 Ids;

            /// <summary>
            /// Storage is mapped further by another 2.5 nibble, making it 5 in total.
            /// </summary>
            [FieldOffset(DbAddressList.Of4.Size)] public DbAddressList.Of1024 Storage;
        }
    }

    /// <summary>
    /// This page is used purely for storage purposes, no ids.
    /// </summary>
    /// <param name="page"></param>
    [method: DebuggerStepThrough]
    public readonly unsafe struct Level2Page(Page page) : IPageWithData<Level2Page>
    {
        public static Level2Page Wrap(Page page) => Unsafe.As<Page, Level2Page>(ref page);

        public void Clear()
        {
            new SlottedArray(Data.Data).Clear();
            Data.Addresses.Clear();
        }

        private const int ConsumedNibbles = 2;

        private ref PageHeader Header => ref page.Header;

        private ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);

        public bool TryGet(IReadOnlyBatchContext batch, scoped in NibblePath key, out ReadOnlySpan<byte> result)
        {
            var map = new SlottedArray(Data.Data);

            if (map.TryGet(key, out result))
            {
                return true;
            }

            var index = GetIndex(key);

            var addr = Data.Addresses[index];
            if (addr.IsNull)
            {
                result = default;
                return false;
            }

            return DataPage.Wrap(batch.GetAt(addr)).TryGet(batch, key.SliceFrom(ConsumedNibbles), out result);
        }

        private static int GetIndex(scoped in NibblePath key) =>
            (key.Nibble0 << NibblePath.NibbleShift) + key.GetAt(1);

        public Page Set(in NibblePath key, in ReadOnlySpan<byte> data, IBatchContext batch)
        {
            if (Header.BatchId != batch.BatchId)
            {
                // the page is from another batch, meaning, it's readonly. Copy
                var writable = batch.GetWritableCopy(page);
                return new Level2Page(writable).Set(key, data, batch);
            }

            var map = new SlottedArray(Data.Data);

            if (map.TrySet(key, data))
            {
                return page;
            }

            // No space in page, flush down
            foreach (var item in map.EnumerateAll())
            {
                var index = GetIndex(item.Key);
                var sliced = item.Key.SliceFrom(ConsumedNibbles);

                var addr = Data.Addresses[index];

                Page child;

                if (addr.IsNull)
                {
                    child = batch.GetNewPage(out addr, true);
                    child.Header.PageType = Header.PageType;
                    child.Header.Level = (byte)(page.Header.Level + ConsumedNibbles);
                }
                else
                {
                    child = batch.GetAt(addr);
                }

                // Set and delete
                Data.Addresses[index] = batch.GetAddress(DataPage.Wrap(child).Set(sliced, item.RawData, batch));
            }

            // All is pushed down
            map.Clear();

            // retry
            return Set(key, data, batch);
        }

        public Page DeleteByPrefix(in NibblePath prefix, IBatchContext batch)
        {
            if (Header.BatchId != batch.BatchId)
            {
                // the page is from another batch, meaning, it's readonly. Copy
                var writable = batch.GetWritableCopy(page);
                return new Level2Page(writable).DeleteByPrefix(prefix, batch);
            }

            var map = new SlottedArray(Data.Data);

            map.DeleteByPrefix(prefix);

            var index = GetIndex(prefix);
            var sliced = prefix.SliceFrom(ConsumedNibbles);

            var addr = Data.Addresses[index];

            if (addr.IsNull)
            {
                return page;
            }

            var child = batch.GetAt(addr);

            // Delete in child
            Data.Addresses[index] = batch.GetAddress(DataPage.Wrap(child).DeleteByPrefix(sliced, batch));

            return page;
        }

        public void Accept(ref NibblePath.Builder builder, IPageVisitor visitor, IPageResolver resolver, DbAddress addr)
        {
            resolver.Prefetch(Data.Addresses);

            using var scope = visitor.On(this, addr);

            for (var i = 0; i < DbAddressList.Of256.Length; i++)
            {
                var bucket = Data.Addresses[i];

                if (!bucket.IsNull)
                {
                    builder.Push((byte)(i >> NibblePath.NibbleShift), (byte)(i & NibblePath.NibbleMask));
                    {
                        DataPage.Wrap(resolver.GetAt(bucket)).Accept(ref builder, visitor, resolver, bucket);
                    }
                    builder.Pop(2);
                }
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = Size)]
        private struct Payload
        {
            private const int Size = Page.PageSize - PageHeader.Size;

            private const int FanOutSize = DbAddressList.Of256.Size;

            private const int DataSize = Size - FanOutSize;

            [FieldOffset(0)] public DbAddressList.Of256 Addresses;

            [FieldOffset(FanOutSize)] private byte DataFirst;

            public Span<byte> Data => MemoryMarshal.CreateSpan(ref DataFirst, DataSize);
        }
    }
}