using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace ApiLookup;

public static class IndexBuilder
{
    public static (IndexStore Store, IndexStats Stats) EnsureBuilt(
        string dbPath,
        string? indexName,
        ResolvedSources sources,
        string sourceDescription,
        bool includeNonPublic,
        bool forceRebuild,
        List<string>? warnings = null)
    {
        var sw = Stopwatch.StartNew();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var effectiveFingerprint = $"{IndexSchema.Version}|{(includeNonPublic ? "all" : "pub")}|{sources.Fingerprint}";

        if (!forceRebuild && TryOpenMatching(dbPath, effectiveFingerprint, out var fastStore))
        {
            var stats = fastStore!.ReadStats(indexName, rebuilt: false, sw.ElapsedMilliseconds);
            return (fastStore, stats);
        }

        using var buildLock = IndexCatalog.AcquireBuildLock(dbPath);

        // Re-check after acquiring the cross-process lock in case another process just completed the build.
        if (!forceRebuild && TryOpenMatching(dbPath, effectiveFingerprint, out var lockedFastStore))
        {
            var stats = lockedFastStore!.ReadStats(indexName, rebuilt: false, sw.ElapsedMilliseconds);
            return (lockedFastStore, stats);
        }

        var tempPath = IndexCatalog.CreateTempBuildPath(dbPath);
        using var producerCts = new CancellationTokenSource();
        Task? producerTask = null;
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            var channel = Channel.CreateBounded<ExtractedAssembly>(new BoundedChannelOptions(Math.Max(4, Environment.ProcessorCount * 2))
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var extractWarnings = new ConcurrentBag<string>();
            var parallelOptions = new ParallelOptions { CancellationToken = producerCts.Token };
            producerTask = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(sources.Assemblies, parallelOptions, async (asm, ct) =>
                    {
                        var localWarnings = new List<string>();
                        var extracted = MetadataExtractor.Extract(asm, includeNonPublic, localWarnings);
                        foreach (var w in localWarnings)
                            extractWarnings.Add(w);
                        if (extracted != null)
                            await channel.Writer.WriteAsync(extracted, ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            });

            using (var conn = new SqliteConnection($"Data Source={tempPath};Mode=ReadWriteCreate;Pooling=False"))
            {
                conn.Open();
                ExecuteNonQuery(conn, @"
PRAGMA journal_mode = OFF;
PRAGMA synchronous = OFF;
PRAGMA temp_store = MEMORY;
PRAGMA cache_size = -64000;
");
                CreateTables(conn);

                using (var tx = conn.BeginTransaction())
                {
                    InsertFromChannel(conn, tx, channel.Reader, indexName, effectiveFingerprint, sourceDescription);
                    producerTask.GetAwaiter().GetResult();
                    tx.Commit();
                }

                CreateIndexes(conn);
                ExecuteNonQuery(conn, @"
INSERT INTO fts_entries(fts_entries) VALUES('optimize');
ANALYZE;
");
            }

            foreach (var w in extractWarnings)
                warnings?.Add(w);

            ReplaceFileWithRetry(tempPath, dbPath);
        }
        catch
        {
            // Unblock producers waiting on the bounded channel so they don't leak when the writer fails.
            producerCts.Cancel();
            try { producerTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        var store = IndexStore.OpenReadOnly(dbPath);
        var builtStats = store.ReadStats(indexName, rebuilt: true, sw.ElapsedMilliseconds);
        return (store, builtStats);
    }

    /// <summary>
    /// Atomically replaces <paramref name="destPath"/>, retrying briefly when another process
    /// (e.g. a concurrent read-only query on Windows) still holds the old database open.
    /// </summary>
    private static void ReplaceFileWithRetry(string sourcePath, string destPath)
    {
        const int maxAttempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Could not replace index database '{destPath}' because it is locked by another process. " +
                    "Close other apilookup queries using this index and retry.", ex);
            }
        }
    }

    private static bool TryOpenMatching(string dbPath, string expectedFingerprint, out IndexStore? store)
    {
        store = null;
        if (!File.Exists(dbPath)) return false;

        IndexStore? candidate = null;
        try
        {
            candidate = IndexStore.OpenReadOnly(dbPath);
            if (candidate.GetMeta("fingerprint") == expectedFingerprint &&
                candidate.GetMeta("schema_version") == IndexSchema.Version)
            {
                store = candidate;
                candidate = null; // ownership transferred to caller
                return true;
            }
        }
        catch
        {
            // Corrupt, foreign, or legacy (pre-meta) database: treat as stale and rebuild.
        }
        finally
        {
            // Always release the handle so the rebuilt temp DB can atomically replace dbPath (Windows sharing rules).
            candidate?.Dispose();
        }
        return false;
    }

    private static void CreateTables(SqliteConnection conn) => ExecuteNonQuery(conn, IndexSchema.CreateTablesSql);

    private static void CreateIndexes(SqliteConnection conn) => ExecuteNonQuery(conn, IndexSchema.CreateIndexesSql);

    private static void InsertFromChannel(
        SqliteConnection conn,
        SqliteTransaction tx,
        ChannelReader<ExtractedAssembly> reader,
        string? indexName,
        string fingerprint,
        string sourceDescription)
    {
        using var metaCmd = conn.CreateCommand();
        metaCmd.Transaction = tx;
        metaCmd.CommandText = "INSERT INTO meta(key, value) VALUES($k, $v)";
        var pMetaK = metaCmd.Parameters.Add("$k", SqliteType.Text);
        var pMetaV = metaCmd.Parameters.Add("$v", SqliteType.Text);

        void PutMeta(string k, string v)
        {
            pMetaK.Value = k;
            pMetaV.Value = v;
            metaCmd.ExecuteNonQuery();
        }

        PutMeta("schema_version", IndexSchema.Version);
        PutMeta("fingerprint", fingerprint);
        PutMeta("built_at_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        PutMeta("sources", sourceDescription);
        if (!string.IsNullOrWhiteSpace(indexName))
            PutMeta("index_name", indexName);

        using var asmCmd = conn.CreateCommand();
        asmCmd.Transaction = tx;
        asmCmd.CommandText = @"
INSERT INTO assemblies(id, name, path, version, xml_path, file_size, mtime_utc, type_count)
VALUES($id, $name, $path, $version, $xml, $size, $mtime, $tcount);";
        var pAsmId = asmCmd.Parameters.Add("$id", SqliteType.Integer);
        var pAsmName = asmCmd.Parameters.Add("$name", SqliteType.Text);
        var pAsmPath = asmCmd.Parameters.Add("$path", SqliteType.Text);
        var pAsmVer = asmCmd.Parameters.Add("$version", SqliteType.Text);
        var pAsmXml = asmCmd.Parameters.Add("$xml", SqliteType.Text);
        var pAsmSize = asmCmd.Parameters.Add("$size", SqliteType.Integer);
        var pAsmMtime = asmCmd.Parameters.Add("$mtime", SqliteType.Integer);
        var pAsmTCount = asmCmd.Parameters.Add("$tcount", SqliteType.Integer);

        using var typeCmd = conn.CreateCommand();
        typeCmd.Transaction = tx;
        typeCmd.CommandText = @"
INSERT INTO types(id, assembly_id, full_name, bare_full_name, clr_name, namespace, name, simple_name, kind,
                  accessibility, modifiers, base_type, base_bare, base_simple, interfaces, generic_params,
                  generic_constraints, delegate_signature, is_extension_container, summary, remarks, doc_id)
VALUES($id, $aid, $full, $bareFull, $clr, $ns, $name, $simple, $kind,
       $acc, $mod, $base, $baseBare, $baseSimple, $ifaces, $gp,
       $gcon, $dsig, $ext, $sum, $rem, $docId);";
        var pTId = typeCmd.Parameters.Add("$id", SqliteType.Integer);
        var pTAid = typeCmd.Parameters.Add("$aid", SqliteType.Integer);
        var pTFull = typeCmd.Parameters.Add("$full", SqliteType.Text);
        var pTBareFull = typeCmd.Parameters.Add("$bareFull", SqliteType.Text);
        var pTClr = typeCmd.Parameters.Add("$clr", SqliteType.Text);
        var pTNs = typeCmd.Parameters.Add("$ns", SqliteType.Text);
        var pTName = typeCmd.Parameters.Add("$name", SqliteType.Text);
        var pTSimple = typeCmd.Parameters.Add("$simple", SqliteType.Text);
        var pTKind = typeCmd.Parameters.Add("$kind", SqliteType.Text);
        var pTAcc = typeCmd.Parameters.Add("$acc", SqliteType.Text);
        var pTMod = typeCmd.Parameters.Add("$mod", SqliteType.Text);
        var pTBase = typeCmd.Parameters.Add("$base", SqliteType.Text);
        var pTBaseBare = typeCmd.Parameters.Add("$baseBare", SqliteType.Text);
        var pTBaseS = typeCmd.Parameters.Add("$baseSimple", SqliteType.Text);
        var pTIfaces = typeCmd.Parameters.Add("$ifaces", SqliteType.Text);
        var pTGp = typeCmd.Parameters.Add("$gp", SqliteType.Text);
        var pTGcon = typeCmd.Parameters.Add("$gcon", SqliteType.Text);
        var pTDsig = typeCmd.Parameters.Add("$dsig", SqliteType.Text);
        var pTExt = typeCmd.Parameters.Add("$ext", SqliteType.Integer);
        var pTSum = typeCmd.Parameters.Add("$sum", SqliteType.Text);
        var pTRem = typeCmd.Parameters.Add("$rem", SqliteType.Text);
        var pTDocId = typeCmd.Parameters.Add("$docId", SqliteType.Text);

        using var ifaceCmd = conn.CreateCommand();
        ifaceCmd.Transaction = tx;
        ifaceCmd.CommandText = "INSERT INTO type_interfaces(type_id, iface_full, iface_simple) VALUES($tid, $full, $simple);";
        var pITid = ifaceCmd.Parameters.Add("$tid", SqliteType.Integer);
        var pIFull = ifaceCmd.Parameters.Add("$full", SqliteType.Text);
        var pISimple = ifaceCmd.Parameters.Add("$simple", SqliteType.Text);

        using var memCmd = conn.CreateCommand();
        memCmd.Transaction = tx;
        memCmd.CommandText = @"
INSERT INTO members(id, type_id, assembly_id, kind, name, accessibility, modifiers, return_type, return_simple,
                    signature, param_types, ext_target_type, ext_target_simple, enum_value, summary, params_doc, returns_doc, doc_id)
VALUES($id, $tid, $aid, $kind, $name, $acc, $mod, $ret, $retS,
       $sig, $ptypes, $extT, $extS, $eval, $sum, $pdoc, $rdoc, $docId);";
        var pMId = memCmd.Parameters.Add("$id", SqliteType.Integer);
        var pMTid = memCmd.Parameters.Add("$tid", SqliteType.Integer);
        var pMAid = memCmd.Parameters.Add("$aid", SqliteType.Integer);
        var pMKind = memCmd.Parameters.Add("$kind", SqliteType.Text);
        var pMName = memCmd.Parameters.Add("$name", SqliteType.Text);
        var pMAcc = memCmd.Parameters.Add("$acc", SqliteType.Text);
        var pMMod = memCmd.Parameters.Add("$mod", SqliteType.Text);
        var pMRet = memCmd.Parameters.Add("$ret", SqliteType.Text);
        var pMRetS = memCmd.Parameters.Add("$retS", SqliteType.Text);
        var pMSig = memCmd.Parameters.Add("$sig", SqliteType.Text);
        var pMPtypes = memCmd.Parameters.Add("$ptypes", SqliteType.Text);
        var pMExtT = memCmd.Parameters.Add("$extT", SqliteType.Text);
        var pMExtS = memCmd.Parameters.Add("$extS", SqliteType.Text);
        var pMEval = memCmd.Parameters.Add("$eval", SqliteType.Text);
        var pMSum = memCmd.Parameters.Add("$sum", SqliteType.Text);
        var pMPdoc = memCmd.Parameters.Add("$pdoc", SqliteType.Text);
        var pMRdoc = memCmd.Parameters.Add("$rdoc", SqliteType.Text);
        var pMDocId = memCmd.Parameters.Add("$docId", SqliteType.Text);

        using var extTargetCmd = conn.CreateCommand();
        extTargetCmd.Transaction = tx;
        extTargetCmd.CommandText = "INSERT INTO extension_targets(member_id, target_full, target_simple) VALUES($mid, $tf, $ts);";
        var pETMid = extTargetCmd.Parameters.Add("$mid", SqliteType.Integer);
        var pETFull = extTargetCmd.Parameters.Add("$tf", SqliteType.Text);
        var pETSimple = extTargetCmd.Parameters.Add("$ts", SqliteType.Text);

        using var typeRefCmd = conn.CreateCommand();
        typeRefCmd.Transaction = tx;
        typeRefCmd.CommandText = "INSERT INTO member_type_refs(member_id, type_full, type_simple, role) VALUES($mid, $tf, $ts, $role);";
        var pRMid = typeRefCmd.Parameters.Add("$mid", SqliteType.Integer);
        var pRTf = typeRefCmd.Parameters.Add("$tf", SqliteType.Text);
        var pRTs = typeRefCmd.Parameters.Add("$ts", SqliteType.Text);
        var pRRole = typeRefCmd.Parameters.Add("$role", SqliteType.Text);

        using var ftsCmd = conn.CreateCommand();
        ftsCmd.Transaction = tx;
        ftsCmd.CommandText = @"
INSERT INTO fts_entries(entity_type, entity_id, kind, assembly_name, namespace, full_name, name, tokens, summary)
VALUES($et, $eid, $kind, $aname, $ns, $full, $name, $tokens, $sum);";
        var pFEt = ftsCmd.Parameters.Add("$et", SqliteType.Text);
        var pFEid = ftsCmd.Parameters.Add("$eid", SqliteType.Integer);
        var pFKind = ftsCmd.Parameters.Add("$kind", SqliteType.Text);
        var pFAname = ftsCmd.Parameters.Add("$aname", SqliteType.Text);
        var pFNs = ftsCmd.Parameters.Add("$ns", SqliteType.Text);
        var pFFull = ftsCmd.Parameters.Add("$full", SqliteType.Text);
        var pFName = ftsCmd.Parameters.Add("$name", SqliteType.Text);
        var pFTokens = ftsCmd.Parameters.Add("$tokens", SqliteType.Text);
        var pFSum = ftsCmd.Parameters.Add("$sum", SqliteType.Text);

        long nextAsmId = 0;
        long nextTypeId = 0;
        long nextMemId = 0;

        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var asm))
            {
                var asmId = ++nextAsmId;
                pAsmId.Value = asmId;
                pAsmName.Value = asm.Name;
                pAsmPath.Value = asm.Path;
                pAsmVer.Value = asm.Version;
                pAsmXml.Value = (object?)asm.XmlPath ?? DBNull.Value;
                pAsmSize.Value = asm.FileSize;
                pAsmMtime.Value = asm.MtimeUtc;
                pAsmTCount.Value = asm.Types.Count;
                asmCmd.ExecuteNonQuery();

                foreach (var t in asm.Types)
                {
                    var typeId = ++nextTypeId;
                    pTId.Value = typeId;
                    pTAid.Value = asmId;
                    pTFull.Value = t.FullName;
                    pTBareFull.Value = t.BareFullName;
                    pTClr.Value = t.ClrName;
                    pTNs.Value = t.Namespace;
                    pTName.Value = t.Name;
                    pTSimple.Value = t.SimpleName;
                    pTKind.Value = t.Kind;
                    pTAcc.Value = t.Accessibility;
                    pTMod.Value = t.Modifiers;
                    pTBase.Value = (object?)t.BaseType ?? DBNull.Value;
                    pTBaseBare.Value = (object?)t.BaseBare ?? DBNull.Value;
                    pTBaseS.Value = (object?)t.BaseSimple ?? DBNull.Value;
                    pTIfaces.Value = t.Interfaces;
                    pTGp.Value = t.GenericParams;
                    pTGcon.Value = t.GenericConstraints;
                    pTDsig.Value = (object?)t.DelegateSignature ?? DBNull.Value;
                    pTExt.Value = t.IsExtensionContainer ? 1 : 0;
                    pTSum.Value = (object?)t.Summary ?? DBNull.Value;
                    pTRem.Value = (object?)t.Remarks ?? DBNull.Value;
                    pTDocId.Value = t.DocId;
                    typeCmd.ExecuteNonQuery();

                    foreach (var iface in t.InterfaceRefs)
                    {
                        pITid.Value = typeId;
                        pIFull.Value = iface.FullName;
                        pISimple.Value = iface.SimpleName;
                        ifaceCmd.ExecuteNonQuery();
                    }

                    var tTokens = Tokenizer.BuildSearchTokens(t.SimpleName, t.FullName, t.BaseSimple, t.Interfaces);
                    var tCjk = Tokenizer.BuildCjkSummaryTokens(t.Summary);
                    if (tCjk.Length > 0)
                        tTokens = string.IsNullOrEmpty(tTokens) ? tCjk : $"{tTokens} {tCjk}";

                    pFEt.Value = EntityTypes.Type;
                    pFEid.Value = typeId;
                    pFKind.Value = t.Kind;
                    pFAname.Value = asm.Name;
                    pFNs.Value = t.Namespace;
                    pFFull.Value = t.FullName;
                    pFName.Value = t.SimpleName;
                    pFTokens.Value = tTokens;
                    pFSum.Value = t.Summary ?? string.Empty;
                    ftsCmd.ExecuteNonQuery();

                    foreach (var m in t.Members)
                    {
                        var memId = ++nextMemId;
                        pMId.Value = memId;
                        pMTid.Value = typeId;
                        pMAid.Value = asmId;
                        pMKind.Value = m.Kind;
                        pMName.Value = m.Name;
                        pMAcc.Value = m.Accessibility;
                        pMMod.Value = m.Modifiers;
                        pMRet.Value = m.ReturnType;
                        pMRetS.Value = m.ReturnSimple;
                        pMSig.Value = m.Signature;
                        pMPtypes.Value = m.ParamTypes;
                        pMExtT.Value = (object?)m.ExtTargetType ?? DBNull.Value;
                        pMExtS.Value = (object?)m.ExtTargetSimple ?? DBNull.Value;
                        pMEval.Value = (object?)m.EnumValue ?? DBNull.Value;
                        pMSum.Value = (object?)m.Summary ?? DBNull.Value;
                        pMPdoc.Value = (object?)m.ParamsDoc ?? DBNull.Value;
                        pMRdoc.Value = (object?)m.ReturnsDoc ?? DBNull.Value;
                        pMDocId.Value = m.DocId;
                        memCmd.ExecuteNonQuery();

                        foreach (var et in m.ExtensionTargets)
                        {
                            pETMid.Value = memId;
                            pETFull.Value = et.TargetFull;
                            pETSimple.Value = et.TargetSimple;
                            extTargetCmd.ExecuteNonQuery();
                        }

                        foreach (var tr in m.TypeRefs)
                        {
                            pRMid.Value = memId;
                            pRTf.Value = tr.TypeFull;
                            pRTs.Value = tr.TypeSimple;
                            pRRole.Value = tr.Role;
                            typeRefCmd.ExecuteNonQuery();
                        }

                        // Skip indexing trivial parameterless constructors in FTS5 (already covered by the type entry)
                        if (m.Kind == MemberKinds.Constructor && string.IsNullOrEmpty(m.ParamTypes))
                            continue;

                        var mTokens = Tokenizer.BuildSearchTokens(m.Name, t.SimpleName, m.ReturnSimple, m.ParamTypes);
                        var mCjk = Tokenizer.BuildCjkSummaryTokens(m.Summary);
                        if (mCjk.Length > 0)
                            mTokens = string.IsNullOrEmpty(mTokens) ? mCjk : $"{mTokens} {mCjk}";

                        pFEt.Value = EntityTypes.Member;
                        pFEid.Value = memId;
                        pFKind.Value = m.Kind;
                        pFAname.Value = asm.Name;
                        pFNs.Value = t.Namespace;
                        pFFull.Value = $"{t.FullName}.{m.Name}";
                        pFName.Value = m.Name;
                        pFTokens.Value = mTokens;
                        pFSum.Value = m.Summary ?? string.Empty;
                        ftsCmd.ExecuteNonQuery();
                    }
                }
            }
        }
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
