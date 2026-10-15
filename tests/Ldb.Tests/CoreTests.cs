using System.Text;
using System.Text.Json;
using Ldb.Core;
using Ldb.Core.Protocol;
using Ldb.Core.Query;
using Ldb.Core.Schema;
using Ldb.Providers.MySql;
using Ldb.Providers.Postgres;
using Ldb.Providers.Sql;

namespace Ldb.Tests;

public class SigningTests
{
    static readonly byte[] Secret = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    // These vectors were computed independently (Python hmac) and are the same ones the bundle's tests use.
    [Fact]
    public void Request_signature_matches_the_published_vector() =>
        Assert.Equal("42zlXLbDTxoL-oJ5eJ3IuG9oGFoByqkaYeBRHtC8x7c",
            Signing.SignRequest(Secret, "/v1/data/find", "main", 1760000000, "AAAAAAAAAAAAAAAAAAAAAA", "{\"namespace\":\"demo\",\"table\":\"players\"}"));

    [Fact]
    public void Response_signature_matches_the_published_vector() =>
        Assert.Equal("Uk6xxTTmVDyXYAuBqMp5aF4vxbPJQH7jjNGM2gs5MH8",
            Signing.SignResponse(Secret, "AAAAAAAAAAAAAAAAAAAAAA", 200, "{\"count\":3}"));

    [Fact]
    public void Changing_any_part_changes_the_signature()
    {
        var baseline = Signing.SignRequest(Secret, "/v1/data/find", "main", 1, "n", "{}");
        Assert.NotEqual(baseline, Signing.SignRequest(Secret, "/v1/data/count", "main", 1, "n", "{}"));
        Assert.NotEqual(baseline, Signing.SignRequest(Secret, "/v1/data/find", "other", 1, "n", "{}"));
        Assert.NotEqual(baseline, Signing.SignRequest(Secret, "/v1/data/find", "main", 2, "n", "{}"));
        Assert.NotEqual(baseline, Signing.SignRequest(Secret, "/v1/data/find", "main", 1, "m", "{}"));
        Assert.NotEqual(baseline, Signing.SignRequest(Secret, "/v1/data/find", "main", 1, "n", "{ }"));
    }

    [Fact]
    public void Equality_check_rejects_different_lengths_and_values()
    {
        Assert.True(Signing.FixedEquals("abc", "abc"));
        Assert.False(Signing.FixedEquals("abc", "abd"));
        Assert.False(Signing.FixedEquals("abc", "abcd"));
    }
}

public class NamesTests
{
    [Theory]
    [InlineData("players", true)]
    [InlineData("player_stats", true)]
    [InlineData("a1", true)]
    [InlineData("Players", false)]
    [InlineData("1players", false)]
    [InlineData("a__b", false)]
    [InlineData("a_", false)]
    [InlineData("a-b", false)]
    [InlineData("a b", false)]
    [InlineData("a\"; drop table x; --", false)]
    [InlineData("", false)]
    public void Identifier_rules(string value, bool valid) => Assert.Equal(valid, Names.IsValid(value, 48));

    [Fact]
    public void Length_limit_is_enforced() => Assert.False(Names.IsValid(new string('a', 25), Names.MaxNamespace));

    [Fact]
    public void Index_names_stay_short_and_stable()
    {
        var name = Names.IndexName("ns__table", new string('x', 40));
        Assert.True(name.Length <= 60);
        Assert.Equal(name, Names.IndexName("ns__table", new string('x', 40)));
    }
}

public class FilterParserTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Empty_filter_means_match_everything()
    {
        Assert.Null(FilterParser.Parse(null));
        Assert.Null(FilterParser.Parse(J("{}")));
    }

    [Fact]
    public void Parses_nested_conditions()
    {
        var node = FilterParser.Parse(J("""{"and":[{"field":"score","op":"gte","value":10},{"or":[{"field":"name","op":"startsWith","value":"A"},{"not":{"field":"banned","op":"eq","value":true}}]}]}"""));
        var and = Assert.IsType<AndNode>(node);
        Assert.Equal(2, and.Children.Count);
        Assert.IsType<OrNode>(and.Children[1]);
    }

    [Theory]
    [InlineData("""{"field":"x","op":"$where","value":1}""")]
    [InlineData("""{"field":"x","op":"eq","value":{"$gt":1}}""")]
    [InlineData("""{"field":"x","op":"eq","value":[1]}""")]
    [InlineData("""{"field":"x; drop","op":"eq","value":1}""")]
    [InlineData("""{"field":"x","op":"eq"}""")]
    [InlineData("""{"field":"x","op":"in","value":[]}""")]
    [InlineData("""{"field":"x","op":"in","value":[{"a":1}]}""")]
    [InlineData("""{"field":"x","op":"eq","value":1,"extra":true}""")]
    [InlineData("""{"and":[]}""")]
    [InlineData("""{"field":"x","op":"contains","value":""}""")]
    [InlineData("""{"field":"x","op":"isNull","value":"yes"}""")]
    public void Rejects_dangerous_or_malformed_filters(string json)
    {
        var ex = Assert.Throws<ApiException>(() => FilterParser.Parse(J(json)));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Rejects_filters_that_are_too_deep()
    {
        var json = """{"field":"x","op":"eq","value":1}""";
        for (var i = 0; i < 10; i++) json = $$"""{"not":{{json}}}""";
        Assert.Throws<ApiException>(() => FilterParser.Parse(J(json)));
    }
}

public class SchemaTests
{
    public static SchemaDefinition Normalize(string json) => SchemaValidator.Normalize(Json.Parse<SchemaDefinition>(json));

    const string V1 = """
    {"tables":[{"name":"players","primaryKey":["id"],"columns":[
      {"name":"id","type":"int64","autoIncrement":true},
      {"name":"name","type":"string","length":32,"unique":true},
      {"name":"score","type":"int32","default":0},
      {"name":"bio","type":"text","nullable":true}]}]}
    """;

    [Fact]
    public void Normalization_folds_unique_into_an_index_and_fills_sizes()
    {
        var s = Normalize(V1);
        var t = s.Tables[0];
        Assert.Contains(t.Indexes!, i => i.Name == "uq_name" && i.Unique && i.Columns.SequenceEqual(new[] { "name" }));
        Assert.All(t.Columns, c => Assert.False(c.Unique));
        Assert.Equal(255, Normalize("""{"tables":[{"name":"t","columns":[{"name":"a","type":"string"}]}]}""").Tables[0].Columns[0].Length);
    }

    [Fact]
    public void Hash_ignores_order_and_rename_hints()
    {
        var a = Normalize("""{"tables":[{"name":"a","columns":[{"name":"x","type":"int32"},{"name":"y","type":"int32"}]},{"name":"b","columns":[{"name":"z","type":"bool"}]}]}""");
        var b = Normalize("""{"tables":[{"name":"b","columns":[{"name":"z","type":"bool"}]},{"name":"a","columns":[{"name":"y","type":"int32","renamedFrom":"old"},{"name":"x","type":"int32"}]}]}""");
        Assert.Equal(SchemaHasher.Hash(a), SchemaHasher.Hash(b));
    }

    [Fact]
    public void Hash_changes_when_the_structure_changes()
    {
        var a = Normalize("""{"tables":[{"name":"a","columns":[{"name":"x","type":"int32"}]}]}""");
        var b = Normalize("""{"tables":[{"name":"a","columns":[{"name":"x","type":"int64"}]}]}""");
        Assert.NotEqual(SchemaHasher.Hash(a), SchemaHasher.Hash(b));
    }

    [Theory]
    [InlineData("""{"tables":[{"name":"a","columns":[]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"varchar"}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"int32"},{"name":"x","type":"int32"}]}]}""")]
    [InlineData("""{"tables":[{"name":"A","columns":[{"name":"x","type":"int32"}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","primaryKey":["nope"],"columns":[{"name":"x","type":"int32"}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"string","length":9999}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"decimal","precision":5,"scale":9}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"string","autoIncrement":true}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"int32","default":"abc"}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"text"}],"indexes":[{"name":"i","columns":["x"]}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"string","length":500}],"indexes":[{"name":"i","columns":["x"]}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","primaryKey":["x"],"columns":[{"name":"x","type":"int32","nullable":true}]}]}""")]
    [InlineData("""{"tables":[{"name":"a","columns":[{"name":"x","type":"string","default":"it's \u0001"}]}]}""")]
    public void Rejects_invalid_schemas(string json)
    {
        var ex = Assert.Throws<ApiException>(() => Normalize(json));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void First_sync_creates_tables_and_indexes()
    {
        var ops = SchemaDiffer.Diff(null, Normalize(V1));
        Assert.Contains(ops, o => o.Kind == SchemaOpKind.CreateTable && o.Table == "players");
        Assert.Contains(ops, o => o.Kind == SchemaOpKind.CreateIndex && o.Index == "uq_name");
        Assert.DoesNotContain(ops, o => o.Destructive);
    }

    [Fact]
    public void Identical_schemas_produce_no_operations() =>
        Assert.Empty(SchemaDiffer.Diff(Normalize(V1), Normalize(V1)));

    [Fact]
    public void Added_changed_and_removed_columns_are_detected()
    {
        var v2 = Normalize("""
        {"tables":[{"name":"players","primaryKey":["id"],"columns":[
          {"name":"id","type":"int64","autoIncrement":true},
          {"name":"name","type":"string","length":16,"unique":true},
          {"name":"score","type":"int32","default":0},
          {"name":"level","type":"int32","default":1}]}]}
        """);
        var ops = SchemaDiffer.Diff(Normalize(V1), v2);
        Assert.Contains(ops, o => o.Kind == SchemaOpKind.AddColumn && o.Column == "level");
        Assert.Contains(ops, o => o.Kind == SchemaOpKind.AlterColumn && o.Column == "name" && o.Destructive);
        Assert.Contains(ops, o => o.Kind == SchemaOpKind.DropColumn && o.Column == "bio" && o.Destructive);
    }

    [Fact]
    public void Widening_is_not_destructive()
    {
        var wider = Normalize(V1.Replace("\"length\":32", "\"length\":64"));
        var op = Assert.Single(SchemaDiffer.Diff(Normalize(V1), wider));
        Assert.Equal(SchemaOpKind.AlterColumn, op.Kind);
        Assert.False(op.Destructive);
    }

    [Fact]
    public void Rename_hint_keeps_the_column_instead_of_dropping_it()
    {
        var renamed = Normalize(V1.Replace("{\"name\":\"bio\",", "{\"name\":\"about\",\"renamedFrom\":\"bio\","));
        var ops = SchemaDiffer.Diff(Normalize(V1), renamed);
        Assert.Contains(ops, o => o.Kind == SchemaOpKind.RenameColumn && o.Column == "bio" && o.NewName == "about");
        Assert.DoesNotContain(ops, o => o.Kind == SchemaOpKind.DropColumn);
    }

    [Fact]
    public void Removed_tables_are_dropped_and_marked_destructive()
    {
        var ops = SchemaDiffer.Diff(Normalize(V1), Normalize("""{"tables":[]}"""));
        var drop = Assert.Single(ops);
        Assert.Equal(SchemaOpKind.DropTable, drop.Kind);
        Assert.True(drop.Destructive);
    }

    [Fact]
    public void Primary_key_changes_are_refused()
    {
        var one = Normalize("""{"tables":[{"name":"t","primaryKey":["a"],"columns":[{"name":"a","type":"int32"},{"name":"b","type":"int32"}]}]}""");
        var two = Normalize("""{"tables":[{"name":"t","primaryKey":["a","b"],"columns":[{"name":"a","type":"int32"},{"name":"b","type":"int32"}]}]}""");
        var ex = Assert.Throws<ApiException>(() => SchemaDiffer.Diff(one, two));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public void Operations_run_in_a_safe_order()
    {
        var v2 = Normalize("""{"tables":[{"name":"players","primaryKey":["id"],"columns":[{"name":"id","type":"int64","autoIncrement":true},{"name":"extra","type":"int32","default":1}]}]}""");
        var kinds = SchemaDiffer.Diff(Normalize(V1), v2).Select(o => o.Kind).ToList();
        Assert.True(kinds.IndexOf(SchemaOpKind.DropIndex) < kinds.IndexOf(SchemaOpKind.DropColumn));
        Assert.True(kinds.IndexOf(SchemaOpKind.AddColumn) < kinds.IndexOf(SchemaOpKind.DropColumn));
    }
}

public class SqlBuilderTests
{
    static readonly TableDefinition Players = SchemaTests.Normalize("""
    {"tables":[{"name":"players","primaryKey":["id"],"columns":[
      {"name":"id","type":"int64","autoIncrement":true},
      {"name":"name","type":"string","length":32},
      {"name":"score","type":"int32","nullable":true},
      {"name":"meta","type":"json","nullable":true}]}]}
    """).Tables[0];

    static JsonElement F(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Values_are_bound_never_inlined()
    {
        var qb = new SqlQueryBuilder(new PostgresDialect());
        var ps = new SqlParams();
        var sql = qb.Where(FilterParser.Parse(F("""{"and":[{"field":"name","op":"eq","value":"x' OR '1'='1"},{"field":"score","op":"in","value":[1,2,3]}]}""")), Players, ps);
        Assert.DoesNotContain("OR '1'", sql);
        Assert.Equal("(\"name\" = @p0 AND \"score\" IN (@p1, @p2, @p3))", sql);
        Assert.Equal("x' OR '1'='1", ps.Items[0].Value);
    }

    [Fact]
    public void Unknown_columns_are_refused()
    {
        var qb = new SqlQueryBuilder(new PostgresDialect());
        var ex = Assert.Throws<ApiException>(() => qb.Where(FilterParser.Parse(F("""{"field":"password","op":"eq","value":"x"}""")), Players, new SqlParams()));
        Assert.Equal("unknown_column", ex.Code);
    }

    [Fact]
    public void Like_patterns_are_escaped()
    {
        var qb = new SqlQueryBuilder(new MySqlDialect());
        var ps = new SqlParams();
        var sql = qb.Where(FilterParser.Parse(F("""{"field":"name","op":"contains","value":"50%_off\\"}""")), Players, ps);
        Assert.Contains("LIKE @p0 ESCAPE", sql);
        Assert.Equal("%50\\%\\_off\\\\%", ps.Items[0].Value);
    }

    [Fact]
    public void Null_comparisons_become_is_null()
    {
        var qb = new SqlQueryBuilder(new PostgresDialect());
        Assert.Equal("\"score\" IS NULL", qb.Where(FilterParser.Parse(F("""{"field":"score","op":"eq","value":null}""")), Players, new SqlParams()));
        Assert.Equal("\"score\" IS NOT NULL", qb.Where(FilterParser.Parse(F("""{"field":"score","op":"isNull","value":false}""")), Players, new SqlParams()));
    }

    [Fact]
    public void Text_operators_need_text_columns()
    {
        var qb = new SqlQueryBuilder(new PostgresDialect());
        Assert.Throws<ApiException>(() => qb.Where(FilterParser.Parse(F("""{"field":"score","op":"contains","value":"1"}""")), Players, new SqlParams()));
    }

    [Fact]
    public void Json_columns_are_cast_in_postgres()
    {
        var qb = new SqlQueryBuilder(new PostgresDialect());
        var sql = qb.Where(FilterParser.Parse(F("""{"field":"meta","op":"eq","value":"{}"}""")), Players, new SqlParams());
        Assert.Contains("CAST(@p0 AS jsonb)", sql);
    }

    [Fact]
    public void Sorting_only_accepts_known_columns_and_directions()
    {
        var qb = new SqlQueryBuilder(new PostgresDialect());
        Assert.Equal(" ORDER BY \"score\" DESC", qb.OrderBy(Players, [new SortSpec("score", "desc")]));
        Assert.Equal(" ORDER BY \"id\"", qb.OrderBy(Players, null));
        Assert.Throws<ApiException>(() => qb.OrderBy(Players, [new SortSpec("score; drop", "asc")]));
        Assert.Throws<ApiException>(() => qb.OrderBy(Players, [new SortSpec("score", "sideways")]));
        Assert.Throws<ApiException>(() => qb.OrderBy(Players, [new SortSpec("meta", "asc")]));
    }

    [Fact]
    public void Value_conversion_rejects_wrong_types_and_long_strings()
    {
        var d = new PostgresDialect();
        var name = Players.Columns.First(c => c.Name == "name");
        Assert.Throws<ApiException>(() => SqlValues.FromJson(name, F("123"), d));
        Assert.Throws<ApiException>(() => SqlValues.FromJson(name, F("\"" + new string('a', 33) + "\""), d));
        Assert.Throws<ApiException>(() => SqlValues.FromJson(name, F("\"a\\u0000b\""), d));
        Assert.Equal("ok", SqlValues.FromJson(name, F("\"ok\""), d));
    }

    [Fact]
    public void Create_table_sql_matches_the_dialect()
    {
        var pg = new PostgresDialect().CreateTableSql("ns__players", Players);
        Assert.Contains("\"id\" BIGINT GENERATED BY DEFAULT AS IDENTITY NOT NULL", pg);
        Assert.Contains("PRIMARY KEY (\"id\")", pg);
        var my = new MySqlDialect().CreateTableSql("ns__players", Players);
        Assert.Contains("`id` BIGINT AUTO_INCREMENT NOT NULL", my);
        Assert.Contains("utf8mb4_bin", my);
    }
}
