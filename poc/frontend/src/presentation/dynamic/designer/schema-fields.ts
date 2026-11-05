import type { ModuleDetail, SchemaField } from 'src/domain/dynamic/types';

/** Active fields of a designer module in the schema shape the condition editor and rule UI use; options keep their `active` flag. */
export function toSchemaFields(module: ModuleDetail): SchemaField[] {
  return module.blocks.flatMap((b) => b.fields)
    .filter((f) => f.status === 'Active')
    .map((f) => ({
      id: f.id, key: f.key, type: f.type, label: f.label, config: f.config,
      options: f.options.map((o) => ({ value: o.value, label: o.label, color: o.color, active: o.active })),
    }));
}

/** Ids of rollup fields and of formulas that (directly or through other formulas) read one: they may not be aggregated or used in a rollup condition. */
export function rollupTainted(module: ModuleDetail): Set<number> {
  const fields = module.blocks.flatMap((b) => b.fields).filter((f) => f.status === 'Active');
  const tainted = new Set(fields.filter((f) => f.type === 'rollup').map((f) => f.id));
  const refs = (f: (typeof fields)[number]) => [...(f.config.formula ?? '').matchAll(/\{(\d+)\}/g)].map((m) => Number(m[1]));
  for (let grew = true; grew;) {
    grew = false;
    for (const f of fields) {
      if (f.type === 'formula' && !tainted.has(f.id) && refs(f).some((id) => tainted.has(id))) { tainted.add(f.id); grew = true; }
    }
  }
  return tainted;
}
