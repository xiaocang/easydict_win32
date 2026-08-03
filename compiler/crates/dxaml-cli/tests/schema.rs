use jsonschema::JSONSchema;
use serde_json::Value;

fn validate_fixture(name: &str) -> Result<(), String> {
    let schema: Value = serde_json::from_str(include_str!("../../../schemas/dxir-v0.schema.json"))
        .expect("dxir schema must be valid JSON");
    let compiled = JSONSchema::compile(&schema).expect("dxir schema must compile");
    compiled.validate(&fixture(name)).map_err(|errors| {
        errors
            .map(|error| error.to_string())
            .collect::<Vec<_>>()
            .join("\n")
    })
}

fn fixture(name: &str) -> Value {
    let source = match name {
        "BindingsValid" => include_str!("fixtures/BindingsValid.dxir.json"),
        "BindingsMissingContext" => include_str!("fixtures/BindingsMissingContext.dxir.json"),
        "BindingsInvalidMode" => include_str!("fixtures/BindingsInvalidMode.dxir.json"),
        "BindingsInvalidSourcePath" => include_str!("fixtures/BindingsInvalidSourcePath.dxir.json"),
        "BindingsEmptyInvalidation" => {
            include_str!("fixtures/BindingsEmptyInvalidation.dxir.json")
        }
        _ => panic!("unknown schema fixture '{name}'"),
    };
    serde_json::from_str(source).expect("schema fixture must be valid JSON")
}

#[test]
fn schema_accepts_valid_one_time_and_one_way_bindings() {
    validate_fixture("BindingsValid")
        .expect("valid binding fixture must satisfy dxir-v0.schema.json");
}

#[test]
fn schema_rejects_malformed_binding_fixtures() {
    for name in [
        "BindingsMissingContext",
        "BindingsInvalidMode",
        "BindingsInvalidSourcePath",
        "BindingsEmptyInvalidation",
    ] {
        assert!(
            validate_fixture(name).is_err(),
            "malformed binding fixture '{name}' unexpectedly satisfied dxir-v0.schema.json"
        );
    }
}
