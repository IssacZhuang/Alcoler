//! Minimal JSON Schema validation for tool inputs (the kernel-side check at
//! the execution entrance).
//!
//! Deliberately restricted to the subset the repo's tool schemas use:
//! `type` (object/string/integer/number/boolean/array/null), `required`,
//! `properties`, `items`. A schema that is not an object (or `{}`) accepts
//! any input, so schema-less tools keep working.

use serde_json::Value;

pub(crate) struct Schema {
    root: Value,
}

impl Schema {
    pub(crate) fn new(root: &Value) -> Self {
        Self { root: root.clone() }
    }

    pub(crate) fn validate(&self, instance: &Value) -> Result<(), String> {
        validate_node(&self.root, instance, "input")
    }
}

fn validate_node(schema: &Value, instance: &Value, path: &str) -> Result<(), String> {
    let Some(spec) = schema.as_object() else {
        return Ok(());
    };

    if let Some(ty) = spec.get("type").and_then(Value::as_str)
        && !type_matches(ty, instance)
    {
        return Err(format!("{path}: expected {ty}"));
    }

    if let (Some(required), Some(obj)) = (
        spec.get("required").and_then(Value::as_array),
        instance.as_object(),
    ) {
        for name in required {
            if let Some(name) = name.as_str()
                && !obj.contains_key(name)
            {
                return Err(format!("{path}: missing required field '{name}'"));
            }
        }
    }

    if let (Some(props), Some(obj)) = (
        spec.get("properties").and_then(Value::as_object),
        instance.as_object(),
    ) {
        for (key, sub) in props {
            if let Some(value) = obj.get(key) {
                validate_node(sub, value, &format!("{path}.{key}"))?;
            }
        }
    }

    if let (Some(items), Some(arr)) = (spec.get("items"), instance.as_array()) {
        for (i, element) in arr.iter().enumerate() {
            validate_node(items, element, &format!("{path}[{i}]"))?;
        }
    }

    Ok(())
}

fn type_matches(ty: &str, value: &Value) -> bool {
    match ty {
        "object" => value.is_object(),
        "string" => value.is_string(),
        "integer" => value.is_i64() || value.is_u64() || value.is_number(),
        "number" => value.is_number(),
        "boolean" => value.is_boolean(),
        "array" => value.is_array(),
        "null" => value.is_null(),
        _ => true,
    }
}
