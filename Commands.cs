using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(WallXrefSync.Plugin))]
[assembly: CommandClass(typeof(WallXrefSync.Commands))]

namespace WallXrefSync
{
    public sealed class Plugin : IExtensionApplication
    {
        public void Initialize() { }
        public void Terminate() { }
    }

    public static class Commands
    {
        private const string AppName = "WALLXREFSYNC";
        private const string ConfigKey = "WALLXREFSYNC_CONFIG_V1";
        private const string SourceLayer = "G-墙";
        private const string BoundaryLayer = "G-墙_同步边界";
        private const string HatchLayer = "G-墙_黑色填充";

        [CommandMethod("WALLSETUP", CommandFlags.Modal)]
        public static void Setup()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            try
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    if (ReadConfig(doc.Database, tr) != null)
                    {
                        ed.WriteMessage("\n本图已配置。运行 WALLSYNC 同步；如需重选范围，先运行 WALLRESET。\n");
                        return;
                    }
                }

                PromptEntityOptions pick = new PromptEntityOptions("\n选择 A 文件的外部参照: ");
                pick.SetRejectMessage("\n请选择外部参照插入对象。\n");
                pick.AddAllowedClass(typeof(BlockReference), false);
                PromptEntityResult selected = ed.GetEntity(pick);
                if (selected.Status != PromptStatus.OK) return;

                string xrefHandle;
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockReference reference = tr.GetObject(selected.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (reference == null) throw new InvalidOperationException("所选对象不是外部参照插入对象。");
                    BlockTableRecord definition = tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    if (definition == null || !definition.IsFromExternalReference)
                        throw new InvalidOperationException("所选块不是外部参照。");
                    xrefHandle = reference.Handle.ToString();
                }

                PromptPointResult first = ed.GetPoint("\n指定处理范围第一个角点: ");
                if (first.Status != PromptStatus.OK) return;
                PromptPointOptions secondOptions = new PromptPointOptions("\n指定处理范围对角点: ")
                {
                    UseBasePoint = true,
                    BasePoint = first.Value
                };
                PromptPointResult second = ed.GetPoint(secondOptions);
                if (second.Status != PromptStatus.OK) return;

                SyncConfig config = new SyncConfig
                {
                    XrefHandle = xrefHandle,
                    MinX = Math.Min(first.Value.X, second.Value.X),
                    MinY = Math.Min(first.Value.Y, second.Value.Y),
                    MaxX = Math.Max(first.Value.X, second.Value.X),
                    MaxY = Math.Max(first.Value.Y, second.Value.Y)
                };

                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(doc.Database, tr);
                    EnsureLayer(doc.Database, tr, BoundaryLayer, plottable: false, colorIndex: 8);
                    EnsureLayer(doc.Database, tr, HatchLayer, plottable: true, colorIndex: 7);
                    WriteConfig(doc.Database, tr, config);
                    tr.Commit();
                }

                Synchronize(doc);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nWALLSETUP 失败: " + ex.Message + "\n");
            }
        }

        [CommandMethod("WALLSYNC", CommandFlags.Modal)]
        public static void Sync()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try { Synchronize(doc); }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\nWALLSYNC 失败: " + ex.Message + "\n"); }
        }

        [CommandMethod("WALLRESET", CommandFlags.Modal)]
        public static void Reset()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            PromptKeywordOptions options = new PromptKeywordOptions("\n删除本插件生成的边界、填充和同步设置? [是/否] <否>: ", "是 否");
            options.Keywords.Default = "否";
            PromptResult answer = ed.GetKeywords(options);
            if (answer.Status != PromptStatus.OK || answer.StringResult != "是") return;

            try
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord modelSpace = GetModelSpace(doc.Database, tr, OpenMode.ForRead);
                    foreach (ObjectId id in modelSpace)
                    {
                        Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || entity.IsErased) continue;
                        if (entity.Layer != BoundaryLayer && entity.Layer != HatchLayer) continue;
                        if (GetMetadata(entity) == null) continue;
                        entity.UpgradeOpen();
                        entity.Erase();
                    }

                    DBDictionary nod = tr.GetObject(doc.Database.NamedObjectsDictionaryId, OpenMode.ForWrite) as DBDictionary;
                    if (nod != null && nod.Contains(ConfigKey))
                    {
                        ObjectId id = nod.GetAt(ConfigKey);
                        nod.Remove(ConfigKey);
                        DBObject record = tr.GetObject(id, OpenMode.ForWrite, false);
                        if (record != null && !record.IsErased) record.Erase();
                    }

                    tr.Commit();
                }
                ed.WriteMessage("\n已删除本插件生成的对象和设置。\n");
            }
            catch (System.Exception ex) { ed.WriteMessage("\nWALLRESET 失败: " + ex.Message + "\n"); }
        }

        private static void Synchronize(Document doc)
        {
            Editor ed = doc.Editor;
            Database hostDb = doc.Database;
            SyncConfig config;
            Matrix3d transform;
            string sourcePath;

            using (Transaction tr = hostDb.TransactionManager.StartTransaction())
            {
                config = ReadConfig(hostDb, tr);
                if (config == null)
                {
                    ed.WriteMessage("\n尚未配置。先运行 WALLSETUP，选择外参和处理范围。\n");
                    return;
                }

                ObjectId referenceId = GetObjectIdFromHandle(hostDb, config.XrefHandle);
                if (referenceId.IsNull || referenceId.IsErased)
                    throw new InvalidOperationException("保存的外参对象已不存在；运行 WALLRESET 后重新 WALLSETUP。");

                BlockReference reference = tr.GetObject(referenceId, OpenMode.ForRead, false) as BlockReference;
                if (reference == null) throw new InvalidOperationException("保存的外参对象无法读取；请重新配置。");
                BlockTableRecord definition = tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                if (definition == null || !definition.IsFromExternalReference)
                    throw new InvalidOperationException("原外参已被替换为普通块；请重新配置。");

                sourcePath = ResolveXrefPath(definition.PathName, hostDb);
                transform = reference.BlockTransform;
            }

            Dictionary<string, WallData> sourceWalls = new Dictionary<string, WallData>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> deferredHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = ReadSourceWalls(sourcePath, transform, config, sourceWalls, deferredHandles);

            int added = 0, updated = 0, removed = 0, unchanged = 0, hatchesCreated = 0;
            List<ObjectId> hatchesToSendBack = new List<ObjectId>();

            using (Transaction tr = hostDb.TransactionManager.StartTransaction())
            {
                EnsureRegApp(hostDb, tr);
                EnsureLayer(hostDb, tr, BoundaryLayer, plottable: false, colorIndex: 8);
                EnsureLayer(hostDb, tr, HatchLayer, plottable: true, colorIndex: 7);

                BlockTableRecord modelSpace = GetModelSpace(hostDb, tr, OpenMode.ForWrite);
                Dictionary<string, ManagedPair> managed = ReadManagedObjects(modelSpace, tr);

                foreach (KeyValuePair<string, WallData> entry in sourceWalls)
                {
                    string handle = entry.Key;
                    WallData wall = entry.Value;
                    ManagedPair pair;
                    if (!managed.TryGetValue(handle, out pair)) pair = new ManagedPair();

                    bool hasBoundary = IsAlive(pair.BoundaryId);
                    bool hasHatch = IsAlive(pair.HatchId);
                    if (!hasBoundary)
                    {
                        pair.BoundaryId = AppendBoundary(modelSpace, tr, hostDb, wall);
                        pair.HatchId = AppendHatch(modelSpace, tr, hostDb, pair.BoundaryId, handle, wall.Signature);
                        hatchesToSendBack.Add(pair.HatchId);
                        added++;
                        continue;
                    }

                    Polyline boundary = tr.GetObject(pair.BoundaryId, OpenMode.ForWrite, false) as Polyline;
                    if (boundary == null)
                    {
                        if (hasHatch) EraseIfAlive(tr, pair.HatchId);
                        pair.BoundaryId = AppendBoundary(modelSpace, tr, hostDb, wall);
                        pair.HatchId = AppendHatch(modelSpace, tr, hostDb, pair.BoundaryId, handle, wall.Signature);
                        hatchesToSendBack.Add(pair.HatchId);
                        updated++;
                        continue;
                    }

                    WallMetadata metadata = GetMetadata(boundary);
                    bool sourceChanged = metadata == null || !String.Equals(metadata.Signature, wall.Signature, StringComparison.Ordinal);
                    bool localWasEdited = !String.Equals(ComputeSignature(ReadWallData(handle, boundary)), wall.Signature, StringComparison.Ordinal);
                    bool changed = sourceChanged || localWasEdited;

                    if (changed && boundary.NumberOfVertices == wall.Points.Length)
                    {
                        UpdateBoundary(boundary, wall);
                        if (hasHatch)
                        {
                            Hatch hatch = tr.GetObject(pair.HatchId, OpenMode.ForWrite, false) as Hatch;
                            if (hatch != null)
                            {
                                hatch.EvaluateHatch(true);
                                SetMetadata(hatch, handle, wall.Signature);
                            }
                            else
                            {
                                pair.HatchId = AppendHatch(modelSpace, tr, hostDb, pair.BoundaryId, handle, wall.Signature);
                                hatchesToSendBack.Add(pair.HatchId);
                                hatchesCreated++;
                            }
                        }
                        else
                        {
                            pair.HatchId = AppendHatch(modelSpace, tr, hostDb, pair.BoundaryId, handle, wall.Signature);
                            hatchesToSendBack.Add(pair.HatchId);
                            hatchesCreated++;
                        }
                        updated++;
                    }
                    else if (changed)
                    {
                        if (hasHatch) EraseIfAlive(tr, pair.HatchId);
                        EraseIfAlive(tr, pair.BoundaryId);
                        pair.BoundaryId = AppendBoundary(modelSpace, tr, hostDb, wall);
                        pair.HatchId = AppendHatch(modelSpace, tr, hostDb, pair.BoundaryId, handle, wall.Signature);
                        hatchesToSendBack.Add(pair.HatchId);
                        updated++;
                    }
                    else
                    {
                        if (!hasHatch)
                        {
                            pair.HatchId = AppendHatch(modelSpace, tr, hostDb, pair.BoundaryId, handle, wall.Signature);
                            hatchesToSendBack.Add(pair.HatchId);
                            hatchesCreated++;
                        }
                        unchanged++;
                    }
                }

                foreach (KeyValuePair<string, ManagedPair> entry in managed)
                {
                    if (sourceWalls.ContainsKey(entry.Key) || deferredHandles.Contains(entry.Key)) continue;
                    if (IsAlive(entry.Value.HatchId)) EraseIfAlive(tr, entry.Value.HatchId);
                    if (IsAlive(entry.Value.BoundaryId)) EraseIfAlive(tr, entry.Value.BoundaryId);
                    removed++;
                }

                MoveHatchesToBottom(modelSpace, tr, hatchesToSendBack);
                tr.Commit();
            }

            ed.WriteMessage("\n墙线同步完成：新增 {0}，调整 {1}，删除 {2}，未变化 {3}，补建填充 {4}，暂缓 {5}。\n",
                added, updated, removed, unchanged, hatchesCreated, deferredHandles.Count);
            if (skipped > 0)
                ed.WriteMessage("跳过 {0} 个不符合条件的源对象；仅处理模型空间中图层“{1}”上的闭合轻量多段线。\n", skipped, SourceLayer);
            if (deferredHandles.Count > 0)
                ed.WriteMessage("暂缓对象保留了旧副本；请检查其几何是否能按外参变换。\n");
        }

        private static int ReadSourceWalls(string path, Matrix3d transform, SyncConfig config,
            Dictionary<string, WallData> walls, HashSet<string> deferredHandles)
        {
            int skipped = 0;
            using (Database sourceDb = new Database(false, true))
            {
                sourceDb.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                sourceDb.CloseInput(true);

                using (Transaction tr = sourceDb.TransactionManager.StartTransaction())
                {
                    BlockTable table = tr.GetObject(sourceDb.BlockTableId, OpenMode.ForRead) as BlockTable;
                    BlockTableRecord modelSpace = tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                    foreach (ObjectId id in modelSpace)
                    {
                        Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || !String.Equals(entity.Layer, SourceLayer, StringComparison.OrdinalIgnoreCase)) continue;
                        Polyline source = entity as Polyline;
                        if (source == null || !source.Closed || source.NumberOfVertices < 3)
                        {
                            skipped++;
                            continue;
                        }

                        string handle = source.Handle.ToString();
                        try
                        {
                            using (Polyline transformed = source.Clone() as Polyline)
                            {
                                if (transformed == null) throw new InvalidOperationException("无法复制多段线。");
                                transformed.TransformBy(transform);
                                if (!Intersects(transformed.GeometricExtents, config)) continue;
                                WallData wall = ReadWallData(handle, transformed);
                                wall.Signature = ComputeSignature(wall);
                                walls[handle] = wall;
                            }
                        }
                        catch
                        {
                            deferredHandles.Add(handle);
                        }
                    }
                    tr.Commit();
                }
            }
            return skipped;
        }

        private static WallData ReadWallData(string sourceHandle, Polyline polyline)
        {
            int count = polyline.NumberOfVertices;
            WallData wall = new WallData
            {
                SourceHandle = sourceHandle,
                Points = new Point2d[count],
                Bulges = new double[count],
                StartWidths = new double[count],
                EndWidths = new double[count],
                Normal = polyline.Normal,
                Elevation = polyline.Elevation
            };
            for (int i = 0; i < count; i++)
            {
                wall.Points[i] = polyline.GetPoint2dAt(i);
                wall.Bulges[i] = polyline.GetBulgeAt(i);
                wall.StartWidths[i] = polyline.GetStartWidthAt(i);
                wall.EndWidths[i] = polyline.GetEndWidthAt(i);
            }
            return wall;
        }

        private static bool Intersects(Extents3d extents, SyncConfig config)
        {
            return extents.MaxPoint.X >= config.MinX && extents.MinPoint.X <= config.MaxX &&
                   extents.MaxPoint.Y >= config.MinY && extents.MinPoint.Y <= config.MaxY;
        }

        private static string ComputeSignature(WallData wall)
        {
            StringBuilder text = new StringBuilder();
            text.Append(Format(wall.Normal.X)).Append('|').Append(Format(wall.Normal.Y)).Append('|')
                .Append(Format(wall.Normal.Z)).Append('|').Append(Format(wall.Elevation));
            for (int i = 0; i < wall.Points.Length; i++)
            {
                text.Append('|').Append(Format(wall.Points[i].X)).Append(',').Append(Format(wall.Points[i].Y))
                    .Append(',').Append(Format(wall.Bulges[i])).Append(',').Append(Format(wall.StartWidths[i]))
                    .Append(',').Append(Format(wall.EndWidths[i]));
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                StringBuilder result = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) result.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static string Format(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

        private static Dictionary<string, ManagedPair> ReadManagedObjects(BlockTableRecord modelSpace, Transaction tr)
        {
            Dictionary<string, ManagedPair> result = new Dictionary<string, ManagedPair>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in modelSpace)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null || entity.IsErased) continue;
                if (entity.Layer != BoundaryLayer && entity.Layer != HatchLayer) continue;
                WallMetadata metadata = GetMetadata(entity);
                if (metadata == null) continue;

                ManagedPair pair;
                if (!result.TryGetValue(metadata.SourceHandle, out pair))
                {
                    pair = new ManagedPair();
                    result.Add(metadata.SourceHandle, pair);
                }
                if (entity.Layer == BoundaryLayer)
                {
                    pair.BoundaryId = id;
                    pair.Signature = metadata.Signature;
                }
                else pair.HatchId = id;
            }
            return result;
        }

        private static ObjectId AppendBoundary(BlockTableRecord modelSpace, Transaction tr, Database db, WallData wall)
        {
            using (Polyline boundary = new Polyline())
            {
                boundary.SetDatabaseDefaults(db);
                boundary.Layer = BoundaryLayer;
                boundary.Normal = wall.Normal;
                boundary.Elevation = wall.Elevation;
                for (int i = 0; i < wall.Points.Length; i++)
                    boundary.AddVertexAt(i, wall.Points[i], wall.Bulges[i], wall.StartWidths[i], wall.EndWidths[i]);
                boundary.Closed = true;
                SetMetadata(boundary, wall.SourceHandle, wall.Signature);
                modelSpace.AppendEntity(boundary);
                tr.AddNewlyCreatedDBObject(boundary, true);
                return boundary.ObjectId;
            }
        }

        private static ObjectId AppendHatch(BlockTableRecord modelSpace, Transaction tr, Database db,
            ObjectId boundaryId, string sourceHandle, string signature)
        {
            using (Hatch hatch = new Hatch())
            {
                hatch.SetDatabaseDefaults(db);
                hatch.Layer = HatchLayer;
                hatch.Color = Color.FromColorIndex(ColorMethod.ByAci, 7);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                modelSpace.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);
                hatch.Associative = true;
                ObjectIdCollection loop = new ObjectIdCollection();
                loop.Add(boundaryId);
                hatch.AppendLoop(HatchLoopTypes.Outermost, loop);
                hatch.EvaluateHatch(true);
                SetMetadata(hatch, sourceHandle, signature);
                return hatch.ObjectId;
            }
        }

        private static void UpdateBoundary(Polyline boundary, WallData wall)
        {
            boundary.Normal = wall.Normal;
            boundary.Elevation = wall.Elevation;
            for (int i = 0; i < wall.Points.Length; i++)
            {
                boundary.SetPointAt(i, wall.Points[i]);
                boundary.SetBulgeAt(i, wall.Bulges[i]);
                boundary.SetStartWidthAt(i, wall.StartWidths[i]);
                boundary.SetEndWidthAt(i, wall.EndWidths[i]);
            }
            boundary.Closed = true;
            SetMetadata(boundary, wall.SourceHandle, wall.Signature);
        }

        private static void MoveHatchesToBottom(BlockTableRecord modelSpace, Transaction tr, List<ObjectId> ids)
        {
            if (ids.Count == 0) return;
            DrawOrderTable drawOrder = tr.GetObject(modelSpace.DrawOrderTableId, OpenMode.ForWrite) as DrawOrderTable;
            if (drawOrder == null) return;
            ObjectIdCollection collection = new ObjectIdCollection();
            foreach (ObjectId id in ids) if (IsAlive(id)) collection.Add(id);
            if (collection.Count > 0) drawOrder.MoveToBottom(collection);
        }

        private static void EraseIfAlive(Transaction tr, ObjectId id)
        {
            if (!IsAlive(id)) return;
            DBObject obj = tr.GetObject(id, OpenMode.ForWrite, false);
            if (obj != null && !obj.IsErased) obj.Erase();
        }

        private static bool IsAlive(ObjectId id) { return !id.IsNull && !id.IsErased; }

        private static WallMetadata GetMetadata(Entity entity)
        {
            ResultBuffer data = entity.GetXDataForApplication(AppName);
            if (data == null) return null;
            using (data)
            {
                TypedValue[] values = data.AsArray();
                if (values.Length < 2) return null;
                string sourceHandle = values[1].Value as string;
                string signature = values.Length > 2 ? values[2].Value as string : String.Empty;
                if (String.IsNullOrEmpty(sourceHandle)) return null;
                return new WallMetadata { SourceHandle = sourceHandle, Signature = signature ?? String.Empty };
            }
        }

        private static void SetMetadata(Entity entity, string sourceHandle, string signature)
        {
            using (ResultBuffer data = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceHandle),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, signature ?? String.Empty)))
            {
                entity.XData = data;
            }
        }

        private static void EnsureRegApp(Database db, Transaction tr)
        {
            RegAppTable table = tr.GetObject(db.RegAppTableId, OpenMode.ForRead) as RegAppTable;
            if (table.Has(AppName)) return;
            table.UpgradeOpen();
            RegAppTableRecord record = new RegAppTableRecord { Name = AppName };
            table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }

        private static void EnsureLayer(Database db, Transaction tr, string name, bool plottable, short colorIndex)
        {
            LayerTable table = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            LayerTableRecord record;
            if (table.Has(name)) record = tr.GetObject(table[name], OpenMode.ForWrite) as LayerTableRecord;
            else
            {
                table.UpgradeOpen();
                record = new LayerTableRecord { Name = name };
                table.Add(record);
                tr.AddNewlyCreatedDBObject(record, true);
            }
            record.IsFrozen = false;
            record.IsOff = false;
            record.IsLocked = false;
            record.IsPlottable = plottable;
            record.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
        }

        private static BlockTableRecord GetModelSpace(Database db, Transaction tr, OpenMode mode)
        {
            BlockTable table = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            return tr.GetObject(table[BlockTableRecord.ModelSpace], mode) as BlockTableRecord;
        }

        private static void WriteConfig(Database db, Transaction tr, SyncConfig config)
        {
            DBDictionary nod = tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite) as DBDictionary;
            Xrecord record;
            if (nod.Contains(ConfigKey)) record = tr.GetObject(nod.GetAt(ConfigKey), OpenMode.ForWrite) as Xrecord;
            else
            {
                record = new Xrecord();
                nod.SetAt(ConfigKey, record);
                tr.AddNewlyCreatedDBObject(record, true);
            }

            using (ResultBuffer data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "1"),
                new TypedValue((int)DxfCode.Text, config.XrefHandle),
                new TypedValue((int)DxfCode.Real, config.MinX),
                new TypedValue((int)DxfCode.Real, config.MinY),
                new TypedValue((int)DxfCode.Real, config.MaxX),
                new TypedValue((int)DxfCode.Real, config.MaxY)))
            {
                record.Data = data;
            }
        }

        private static SyncConfig ReadConfig(Database db, Transaction tr)
        {
            DBDictionary nod = tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead) as DBDictionary;
            if (nod == null || !nod.Contains(ConfigKey)) return null;
            Xrecord record = tr.GetObject(nod.GetAt(ConfigKey), OpenMode.ForRead) as Xrecord;
            if (record == null) return null;
            using (ResultBuffer data = record.Data)
            {
                if (data == null) return null;
                TypedValue[] values = data.AsArray();
                if (values.Length < 6 || !String.Equals(values[0].Value as string, "1", StringComparison.Ordinal)) return null;
                return new SyncConfig
                {
                    XrefHandle = values[1].Value as string,
                    MinX = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture),
                    MinY = Convert.ToDouble(values[3].Value, CultureInfo.InvariantCulture),
                    MaxX = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture),
                    MaxY = Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture)
                };
            }
        }

        private static ObjectId GetObjectIdFromHandle(Database db, string handleText)
        {
            ulong raw = UInt64.Parse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return db.GetObjectId(false, new Handle(unchecked((long)raw)), 0);
        }

        private static string ResolveXrefPath(string path, Database hostDb)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new FileNotFoundException("外参没有有效的 DWG 路径。");
            if (Path.IsPathRooted(path) && File.Exists(path)) return Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(hostDb.Filename);
            if (!String.IsNullOrEmpty(directory))
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, path));
                if (File.Exists(candidate)) return candidate;
            }
            if (File.Exists(path)) return Path.GetFullPath(path);
            throw new FileNotFoundException("找不到外参源文件: " + path + "。请修复 B 图中的外参路径后再同步。");
        }

        private sealed class SyncConfig
        {
            public string XrefHandle;
            public double MinX, MinY, MaxX, MaxY;
        }

        private sealed class WallData
        {
            public string SourceHandle;
            public string Signature;
            public Point2d[] Points;
            public double[] Bulges, StartWidths, EndWidths;
            public Vector3d Normal;
            public double Elevation;
        }

        private sealed class WallMetadata
        {
            public string SourceHandle;
            public string Signature;
        }

        private sealed class ManagedPair
        {
            public ObjectId BoundaryId = ObjectId.Null;
            public ObjectId HatchId = ObjectId.Null;
            public string Signature = String.Empty;
        }
    }
}
