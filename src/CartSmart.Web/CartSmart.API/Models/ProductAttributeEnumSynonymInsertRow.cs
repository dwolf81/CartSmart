using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("product_attribute_enum_synonym")]
public class ProductAttributeEnumSynonymInsertRow : BaseModel
{
    [Column("product_id")]
    public long ProductId { get; set; }

    [Column("attribute_id")]
    public int AttributeId { get; set; }

    [Column("enum_value_id")]
    public int EnumValueId { get; set; }

    [Column("synonym")]
    public string Synonym { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
