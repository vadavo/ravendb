using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Sparrow.Server.Compression;
using Voron;
using Voron.Impl;
using Voron.Data.Containers;
using Sparrow;
using System.Runtime.Intrinsics.X86;
using Corax.Pipeline;
using Corax.Queries;
using Sparrow.Server;

namespace Corax;

public sealed unsafe partial class IndexSearcher : IDisposable
{
    private readonly Transaction _transaction;
    private readonly IndexFieldsMapping _fieldMapping;

    private Page _lastPage = default;

    /// <summary>
    /// When true no SIMD instruction will be used. Useful for checking that optimized algorithms behave in the same
    /// way than reference algorithms. 
    /// </summary>
    public bool ForceNonAccelerated { get; set; }

    public bool IsAccelerated => Avx2.IsSupported && !ForceNonAccelerated;

    public long NumberOfEntries => _transaction.LowLevelTransaction.RootObjects.ReadInt64(Constants.IndexWriter.NumberOfEntriesSlice) ?? 0;

    internal ByteStringContext Allocator => _transaction.Allocator;

    internal Transaction Transaction => _transaction;


    private readonly bool _ownsTransaction;

    // The reason why we want to have the transaction open for us is so that we avoid having
    // to explicitly provide the index searcher with opening semantics and also every new
    // searcher becomes essentially a unit of work which makes reusing assets tracking more explicit.
    public IndexSearcher(StorageEnvironment environment, IndexFieldsMapping fieldsMapping = null)
    {
        _ownsTransaction = true;
        _transaction = environment.ReadTransaction();
        _fieldMapping = fieldsMapping ?? new IndexFieldsMapping(_transaction.Allocator);
    }

    public IndexSearcher(Transaction tx, IndexFieldsMapping fieldsMapping = null)
    {
        _ownsTransaction = false;
        _transaction = tx;
        _fieldMapping = fieldsMapping ?? new IndexFieldsMapping(_transaction.Allocator);
    }

    public UnmanagedSpan GetIndexEntryPointer(long id)
    {
        var data = Container.MaybeGetFromSamePage(_transaction.LowLevelTransaction, ref _lastPage, id);
        int size = ZigZagEncoding.Decode<int>(data.ToSpan(), out var len);
        return data.ToUnmanagedSpan().Slice(size + len);
    }

    public IndexEntryReader GetReaderFor(long id)
    {
        return GetReaderFor(_transaction, ref _lastPage, id);
    }

    public static IndexEntryReader GetReaderFor(Transaction transaction, ref Page page, long id)
    {
        var data = Container.MaybeGetFromSamePage(transaction.LowLevelTransaction, ref page, id).ToSpan();
        int size = ZigZagEncoding.Decode<int>(data, out var len);
        return new IndexEntryReader(data.Slice(size + len));
    }

    public string GetIdentityFor(long id)
    {
        var data = Container.MaybeGetFromSamePage(_transaction.LowLevelTransaction, ref _lastPage, id).ToSpan();
        int size = ZigZagEncoding.Decode<int>(data, out var len);
        return Encoding.UTF8.GetString(data.Slice(len, size));
    }
    
    [SkipLocalsInit]
    private Slice EncodeAndApplyAnalyzer(string term, int fieldId)
    {
        if (term is null)
            return default;
        
        var encoded = Encoding.UTF8.GetBytes(term);
        Slice termSlice;
        if (fieldId == Constants.IndexSearcher.NonAnalyzer)
        {
            Slice.From(Allocator, encoded, out termSlice);
            return termSlice;
        }

        Slice.From(Allocator, ApplyAnalyzer(encoded, fieldId), out termSlice);
        return termSlice;
    }

    //todo maciej: notice this is very inefficient. We need to improve it in future. 
    // Only for KeywordTokenizer
    [SkipLocalsInit]
    private unsafe ReadOnlySpan<byte> ApplyAnalyzer(ReadOnlySpan<byte> originalTerm, int fieldId)
    {
        if (_fieldMapping.Count == 0)
            return originalTerm;

        if (_fieldMapping.TryGetByFieldId(fieldId, out var binding) == false)
            return originalTerm;

        var analyzer = binding.Analyzer;

        if (analyzer is null)
            return originalTerm;

        analyzer.GetOutputBuffersSize(originalTerm.Length, out int outputSize, out int tokenSize);

        Span<byte> encoded = new byte[outputSize];
        Token* tokensPtr = stackalloc Token[tokenSize];
        var tokens = new Span<Token>(tokensPtr, tokenSize);
        analyzer.Execute(originalTerm, ref encoded, ref tokens);

        return encoded;
    }

    public AllEntriesMatch AllEntries()
    {
        return new AllEntriesMatch(_transaction);
    }

    public void Dispose()
    {
        if (_ownsTransaction)
            _transaction?.Dispose();
    }
}
