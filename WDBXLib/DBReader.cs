﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.IO;
using System.Text;
using WDBXLib.FileTypes;
using WDBXLib;

namespace WDBXLib
{
    public class DBReader
    {
        public string ErrorMessage { get; set; }

        private List<Tuple<int, short>> OffsetMap = new List<Tuple<int, short>>();
        private string FileName;

        #region Read Methods
        private DBHeader ExtractHeader(BinaryReader dbReader)
        {
            DBHeader header = null;
            string signature = dbReader.ReadString(4);

            if (string.IsNullOrWhiteSpace(signature))
                return null;

            if (signature[0] != 'W')
                signature = new string(signature.ToCharArray().Reverse().ToArray());

            switch (signature)
            {
                case "WDBC":
                    header = new WDBC();
                    break;
                case "WDB2":
                case "WCH2":
                    header = new WDB2();
                    break;
                case "WDB5":
                    header = new WDB5();
                    break;
            }

            header?.ReadHeader(ref dbReader, signature);
            return header;
        }

        public DBFile<T> Read<T>(MemoryStream stream, string dbFile) where T : class
        {
            FileName = dbFile;
            stream.Position = 0;

            using (var dbReader = new BinaryReader(stream, Encoding.UTF8))
            {
                DBHeader header = ExtractHeader(dbReader);
                long pos = dbReader.BaseStream.Position;

                //No header - must be invalid
                if (!(header?.IsValidFile ?? false))
                    throw new Exception("Unknown file type.");

                if (header.RecordCount == 0 || header.RecordSize == 0)
                    throw new Exception("File contains no records.");

                DBFile<T> entry = new DBFile<T>(header, dbFile);
                if (entry.TableStructure == null)
                    throw new Exception("Definition missing.");

                if (header.IsTypeOf<WDBC>() || header.IsTypeOf<WDB2>())
                {
                    long stringTableStart = dbReader.BaseStream.Position += header.RecordCount * header.RecordSize;
                    Dictionary<int, string> StringTable = new StringTable().Read(dbReader, stringTableStart); //Get stringtable
                    dbReader.Scrub(pos);

                    ReadIntoTable(ref entry, dbReader, StringTable); //Read data

                    stream.Dispose();
                    return entry;
                }
                else if (header.IsTypeOf<WDB5>())
                {
                    WDB5 wdb5 = (header as WDB5);

                    int CopyTableSize = wdb5?.CopyTableSize ?? 0; //Only WDB5 has a copy table

                    //StringTable - only if applicable
                    long copyTablePos = dbReader.BaseStream.Length - CopyTableSize;
                    long indexTablePos = copyTablePos - (header.HasIndexTable ? header.RecordCount * 4 : 0);
                    long stringTableStart = indexTablePos - header.StringBlockSize;
                    Dictionary<int, string> StringTable = new Dictionary<int, string>();
                    if (!header.HasOffsetTable) //Stringtable is only present if there isn't an offset map
                    {
                        dbReader.Scrub(stringTableStart);
                        StringTable = new StringTable().Read(dbReader, stringTableStart, stringTableStart + header.StringBlockSize);
                        dbReader.Scrub(pos);
                    }

                    //Read the data
                    using (MemoryStream ms = new MemoryStream(header.ReadData(dbReader, pos)))
                    using (BinaryReader dataReader = new BinaryReader(ms, Encoding.UTF8))
                    {
                        ReadIntoTable(ref entry, dataReader, StringTable);
                    }

                    //Cleanup
                    if (header.IsTypeOf<WDB5>())
                        wdb5.OffsetLengths = null;

                    stream.Dispose();
                    return entry;
                }
                else
                {
                    stream.Dispose();
                    throw new Exception($"Invalid filetype.");
                }
            }
        }

        public DBFile<T> Read<T>(string dbFile) where T : class
        {
            return Read<T>(new MemoryStream(File.ReadAllBytes(dbFile)), dbFile);
        }

        private void ReadIntoTable<T>(ref DBFile<T> entry, BinaryReader dbReader, Dictionary<int, string> StringTable)
        {
            if (entry.Header.RecordCount == 0)
                return;

            TypeCode[] columnTypes = entry.Data.Columns.Cast<DataColumn>().Select(x => Type.GetTypeCode(x.DataType)).ToArray();

            FieldStructureEntry[] bits = entry.GetBits();
            uint recordsize = entry.Header.RecordSize + (uint)(entry.Header.HasIndexTable ? 4 : 0);
            int recordcount = Math.Max(entry.Header.OffsetLengths.Length, (int)entry.Header.RecordCount);

            entry.Data.BeginLoadData();

            for (uint i = 0; i < recordcount; i++)
            {
                //Offset map has variable record lengths
                if (entry.Header.HasOffsetTable)
                    recordsize = (uint)entry.Header.OffsetLengths[i];

                //Store start position
                long offset = dbReader.BaseStream.Position;

                //Create row and add data
                var row = entry.Data.NewRow();
                for (int j = 0; j < entry.Data.Columns.Count; j++)
                {
                    if (entry.Data.Columns[j].ExtendedProperties.ContainsKey("AUTO_GENERATED"))
                    {
                        row.SetField(entry.Data.Columns[j], entry.Data.Rows.Count + 1);
                        continue;
                    }

                    switch (columnTypes[j])
                    {
                        case TypeCode.Boolean:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadBoolean());
                            break;
                        case TypeCode.SByte:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadSByte());
                            break;
                        case TypeCode.Byte:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadByte());
                            break;
                        case TypeCode.Int16:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadInt16());
                            break;
                        case TypeCode.UInt16:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadUInt16());
                            break;
                        case TypeCode.Int32:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadInt32(bits[j]));
                            break;
                        case TypeCode.UInt32:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadUInt32(bits[j]));
                            break;
                        case TypeCode.Int64:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadInt64(bits[j]));
                            break;
                        case TypeCode.UInt64:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadUInt64(bits[j]));
                            break;
                        case TypeCode.Single:
                            row.SetField(entry.Data.Columns[j], dbReader.ReadSingle());
                            break;
                        case TypeCode.String:
                            if (entry.Header.HasOffsetTable)
                                row.SetField(entry.Data.Columns[j], dbReader.ReadStringNull());
                            else
                            {
                                int stindex = dbReader.ReadInt32();
                                try { row.SetField(entry.Data.Columns[j], StringTable[stindex]); }
                                catch
                                {
                                    row.SetField(entry.Data.Columns[j], "String not found");
                                    ErrorMessage = "Strings not found in string table";
                                }
                            }
                            break;
                        default:
                            dbReader.BaseStream.Position += 4;
                            break;
                    }
                }

                entry.Data.Rows.Add(row);

                //Scrub to the end of the record
                if (dbReader.BaseStream.Position - offset < recordsize)
                    dbReader.BaseStream.Position += (recordsize - (dbReader.BaseStream.Position - offset));
                else if (dbReader.BaseStream.Position - offset > recordsize)
                    throw new Exception("Definition exceeds record size");
            }

            entry.Data.EndLoadData();
        }
        #endregion

        #region Write Methods
        public void Write<T>(DBFile<T> entry, string savepath)
        {
            using (var fs = new FileStream(savepath, FileMode.Create))
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                StringTable st = new StringTable(entry.Header.ExtendedStringTable); //Preloads null byte(s)
                entry.Header.WriteHeader(bw, entry);

                if (entry.Header.IsTypeOf<WDB5>() && !entry.Header.HasOffsetTable)
                    ReadIntoFile(entry, bw, entry.GetUniqueRows().ToArray(), ref st); //Insert unique rows
                else
                    ReadIntoFile(entry, bw, entry.Data.AsEnumerable(), ref st); //Insert all rows

                //Copy StringTable and StringTable size if it doesn't have inline strings
                if (st.Size > 0 && !entry.Header.HasOffsetTable)
                {
                    long pos = bw.BaseStream.Position;
                    bw.Scrub(entry.Header.StringTableOffset);
                    bw.Write(st.Size);
                    bw.Scrub(pos);
                    st.CopyTo(bw.BaseStream);
                }
                st.Dispose();

                //Legion+ only
                if (entry.Header.IsLegionFile)
                {
                    //OffsetMap
                    if (entry.Header.HasOffsetTable)
                    {
                        //Update StringTableOffset with current position
                        long pos = bw.BaseStream.Position;
                        bw.Scrub(entry.Header.StringTableOffset);
                        bw.Write((int)pos);
                        bw.Scrub(pos);

                        entry.Header.WriteOffsetMap(bw, entry, OffsetMap);

                        OffsetMap.Clear(); //Cleanup
                    }

                    //Index Table
                    if (entry.Header.HasIndexTable)
                        entry.Header.WriteIndexTable(bw, entry);

                    //CopyTable - WDB5 only
                    if (entry.Header.IsTypeOf<WDB5>())
                        ((WDB5)entry.Header).WriteCopyTable(bw, entry);
                }

                //Copy data to file
                ms.Position = 0;
                ms.CopyTo(fs);
            }
        }

        private void ReadIntoFile<T>(DBFile<T> entry, BinaryWriter bw, IEnumerable<DataRow> rows, ref StringTable st)
        {
            TypeCode[] columnTypes = entry.Data.Columns.Cast<DataColumn>().Select(x => Type.GetTypeCode(x.DataType)).ToArray();
            var bits = entry.GetBits();

            bool duplicates = false;
            if (entry.Header.IsTypeOf<WDB2>() && ((WDB2)entry.Header).MaxId != 0) //WDB2 with MaxId > 0 allows duplicates
                duplicates = true;

            var lastrow = entry.Data.Rows[entry.Data.Rows.Count - 1];

            foreach (DataRow row in rows)
            {
                long offset = bw.BaseStream.Position;

                for (int j = 0; j < entry.Data.Columns.Count; j++)
                {
                    if (entry.Data.Columns[j].ExtendedProperties.ContainsKey("AUTO_GENERATED")) //Autogenerated so skip
                        continue;

                    if (entry.Header.HasIndexTable && j == 0) //Inline Id so skip
                        continue;

                    switch (columnTypes[j])
                    {
                        case TypeCode.SByte:
                            bw.Write(row.Field<sbyte>(j));
                            break;
                        case TypeCode.Byte:
                            bw.Write(row.Field<byte>(j));
                            break;
                        case TypeCode.Int16:
                            bw.Write(row.Field<short>(j));
                            break;
                        case TypeCode.UInt16:
                            bw.Write(row.Field<ushort>(j));
                            break;
                        case TypeCode.Int32:
                            bw.WriteInt32(row.Field<int>(j), bits?[j]);
                            break;
                        case TypeCode.UInt32:
                            bw.WriteUInt32(row.Field<uint>(j), bits?[j]);
                            break;
                        case TypeCode.Int64:
                            bw.WriteInt64(row.Field<long>(j), bits?[j]);
                            break;
                        case TypeCode.UInt64:
                            bw.WriteUInt64(row.Field<ulong>(j), bits?[j]);
                            break;
                        case TypeCode.Single:
                            bw.Write(row.Field<float>(j));
                            break;
                        case TypeCode.String:
                            if (entry.Header.HasOffsetTable)
                            {
                                bw.Write(Encoding.UTF8.GetBytes(row.Field<string>(j)));
                                bw.Write((byte)0);
                            }
                            else
                                bw.Write(st.Write(row.Field<string>(j), duplicates));
                            break;
                        default:
                            throw new Exception($"Unknown TypeCode {columnTypes[j].ToString()}");
                    }
                }

                //Calculate and write the row's padding
                entry.Header.WriteRecordPadding(bw, entry, offset);

                //Store the offset map
                if (entry.Header.HasOffsetTable)
                    OffsetMap.Add(new Tuple<int, short>((int)offset, (short)(bw.BaseStream.Position - offset)));

                //WDB5 + OffsetMap without SecondIndex for the last row pads to next mod 4
                if (entry.Header.IsTypeOf<WDB5>() && entry.Header.HasOffsetTable && !entry.Header.HasSecondIndex && row == lastrow)
                {
                    long rem = bw.BaseStream.Position % 4;
                    bw.BaseStream.Position += (rem == 0 ? 0 : (4 - rem));
                }
            }
        }
        #endregion

    }
}