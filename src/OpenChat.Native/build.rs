use std::env;
use std::path::PathBuf;

fn main() {
    let crate_dir = env::var("CARGO_MANIFEST_DIR").unwrap();
    let output_dir = PathBuf::from(&crate_dir)
        .parent()
        .unwrap()
        .join("OpenChat.Core")
        .join("Marmot")
        .join("Generated");

    // Create output directory if it doesn't exist
    std::fs::create_dir_all(&output_dir).ok();

    csbindgen::Builder::default()
        .input_extern_file("src/lib.rs")
        .input_extern_file("src/client.rs")
        .input_extern_file("src/group.rs")
        .input_extern_file("src/error.rs")
        .csharp_dll_name("openchat_native")
        .csharp_namespace("OpenChat.Core.Marmot.Generated")
        .csharp_class_name("MarmotNative")
        .csharp_class_accessibility("internal")
        .generate_csharp_file(output_dir.join("MarmotNative.g.cs"))
        .unwrap();

    println!("cargo:rerun-if-changed=src/lib.rs");
    println!("cargo:rerun-if-changed=src/client.rs");
    println!("cargo:rerun-if-changed=src/group.rs");
    println!("cargo:rerun-if-changed=src/error.rs");
}
