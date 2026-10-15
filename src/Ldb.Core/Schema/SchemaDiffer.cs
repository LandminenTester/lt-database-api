namespace Ldb.Core.Schema;

/// <summary>
/// Compares the previously applied schema with the desired one and produces an ordered plan.
/// Renames are only recognised through an explicit RenamedFrom hint, everything else that disappears is a drop.
/// </summary>
public static class SchemaDiffer
{
    public static List<SchemaOp> Diff(SchemaDefinition? previous, SchemaDefinition desired)
    {
        var ops = new List<SchemaOp>();
        var prevTables = (previous?.Tables ?? []).ToDictionary(t => t.Name);
        var desiredNames = desired.Tables.Select(t => t.Name).ToHashSet();
        var matchedPrev = new HashSet<string>();

        foreach (var d in desired.Tables)
        {
            TableDefinition? p = null;
            if (prevTables.TryGetValue(d.Name, out var same)) p = same;
            else if (d.RenamedFrom is { } from && prevTables.TryGetValue(from, out var old) && !desiredNames.Contains(from) && !matchedPrev.Contains(from))
            {
                p = old;
                ops.Add(new SchemaOp { Kind = SchemaOpKind.RenameTable, Table = from, NewName = d.Name, Note = $"rename table {from} to {d.Name}" });
            }

            if (p is null)
            {
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateTable, Table = d.Name, TableDef = d });
                foreach (var ix in d.Indexes ?? [])
                    ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = d.Name, Index = ix.Name, IndexDef = ix });
                continue;
            }
            matchedPrev.Add(p.Name);
            DiffTable(ops, p, d);
        }

        foreach (var p in prevTables.Values.Where(p => !matchedPrev.Contains(p.Name)))
            ops.Add(new SchemaOp { Kind = SchemaOpKind.DropTable, Table = p.Name, Destructive = true, TableDef = p, Note = "table removed from the schema" });

        return [.. ops.OrderBy(o => (int)o.Kind)];
    }

    static void DiffTable(List<SchemaOp> ops, TableDefinition p, TableDefinition d)
    {
        // After a table rename the operations use the new name.
        var table = d.Name;
        if (!(p.PrimaryKey ?? []).SequenceEqual(d.PrimaryKey ?? []))
            throw new ApiException(409, "unsupported_change", $"Table '{table}': changing the primary key is not supported. Create a new table and migrate the data.");

        var prevCols = p.Columns.ToDictionary(c => c.Name);
        var desiredColNames = d.Columns.Select(c => c.Name).ToHashSet();
        var consumed = new HashSet<string>();

        foreach (var dc in d.Columns)
        {
            if (prevCols.TryGetValue(dc.Name, out var pc))
            {
                consumed.Add(pc.Name);
                if (!Same(pc, dc))
                    ops.Add(new SchemaOp { Kind = SchemaOpKind.AlterColumn, Table = table, Column = dc.Name, ColumnDef = dc, OldColumnDef = pc, Destructive = Narrows(pc, dc) });
            }
            else if (dc.RenamedFrom is { } from && prevCols.TryGetValue(from, out var renamed) && !desiredColNames.Contains(from) && !consumed.Contains(from))
            {
                consumed.Add(from);
                ops.Add(new SchemaOp { Kind = SchemaOpKind.RenameColumn, Table = table, Column = from, NewName = dc.Name, Note = $"rename column {from} to {dc.Name}" });
                if (!Same(renamed with { Name = dc.Name }, dc))
                    ops.Add(new SchemaOp { Kind = SchemaOpKind.AlterColumn, Table = table, Column = dc.Name, ColumnDef = dc, OldColumnDef = renamed with { Name = dc.Name }, Destructive = Narrows(renamed, dc) });
            }
            else ops.Add(new SchemaOp { Kind = SchemaOpKind.AddColumn, Table = table, Column = dc.Name, ColumnDef = dc });
        }
        foreach (var pc in p.Columns.Where(c => !consumed.Contains(c.Name)))
            ops.Add(new SchemaOp { Kind = SchemaOpKind.DropColumn, Table = table, Column = pc.Name, OldColumnDef = pc, Destructive = true, Note = "column removed from the schema" });

        var prevIx = (p.Indexes ?? []).ToDictionary(i => i.Name);
        var desiredIx = (d.Indexes ?? []).ToDictionary(i => i.Name);
        foreach (var (name, ix) in desiredIx)
        {
            if (!prevIx.TryGetValue(name, out var old))
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = table, Index = name, IndexDef = ix });
            else if (old.Unique != ix.Unique || !old.Columns.SequenceEqual(ix.Columns))
            {
                ops.Add(new SchemaOp { Kind = SchemaOpKind.DropIndex, Table = table, Index = name, IndexDef = old });
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = table, Index = name, IndexDef = ix });
            }
        }
        foreach (var (name, ix) in prevIx.Where(x => !desiredIx.ContainsKey(x.Key)))
            ops.Add(new SchemaOp { Kind = SchemaOpKind.DropIndex, Table = table, Index = name, IndexDef = ix });
    }

    static bool Same(ColumnDefinition a, ColumnDefinition b) =>
        a.Type == b.Type && a.Length == b.Length && a.Precision == b.Precision && a.Scale == b.Scale
        && a.Nullable == b.Nullable && a.AutoIncrement == b.AutoIncrement
        && (a.Default?.GetRawText() ?? "") == (b.Default?.GetRawText() ?? "");

    /// <summary>A change that can lose or reject existing data: another type, a shorter or less precise column, or NOT NULL.</summary>
    public static bool Narrows(ColumnDefinition o, ColumnDefinition n)
    {
        if (o.Type != n.Type) return !(o.Type == ColumnTypes.Int32 && n.Type == ColumnTypes.Int64) && !(o.Type == ColumnTypes.String && n.Type == ColumnTypes.Text);
        if (o.Length is { } ol && n.Length is { } nl && nl < ol) return true;
        if (o.Precision is { } op && n.Precision is { } np && (np < op || n.Scale < o.Scale)) return true;
        return o.Nullable && !n.Nullable;
    }
}
