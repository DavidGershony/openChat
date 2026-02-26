//! OpenChat Native Library
//!
//! This library provides C-compatible FFI bindings for MLS group messaging
//! using the Marmot protocol over Nostr.

mod client;
mod error;
// mod group; // Not needed - using MDK directly

use std::ffi::{c_char, c_int, CStr, CString};
use std::ptr;
use std::slice;
use std::sync::Mutex;

use once_cell::sync::Lazy;

use client::MarmotClient;

/// Thread-local storage for the last error message
static LAST_ERROR: Lazy<Mutex<Option<String>>> = Lazy::new(|| Mutex::new(None));

fn set_last_error(error: impl ToString) {
    if let Ok(mut guard) = LAST_ERROR.lock() {
        *guard = Some(error.to_string());
    }
}

fn clear_last_error() {
    if let Ok(mut guard) = LAST_ERROR.lock() {
        *guard = None;
    }
}

/// Get the last error message.
/// Returns null if no error occurred.
/// The caller must free the returned string using `marmot_free_string`.
#[no_mangle]
pub extern "C" fn marmot_get_last_error() -> *mut c_char {
    let guard = match LAST_ERROR.lock() {
        Ok(g) => g,
        Err(_) => return ptr::null_mut(),
    };

    match &*guard {
        Some(error) => match CString::new(error.as_str()) {
            Ok(s) => s.into_raw(),
            Err(_) => ptr::null_mut(),
        },
        None => ptr::null_mut(),
    }
}

/// Create a new Marmot client with the given Nostr identity.
///
/// # Arguments
/// * `private_key_hex` - The Nostr private key in hex format
/// * `public_key_hex` - The Nostr public key in hex format
///
/// # Returns
/// A pointer to the client, or null on failure.
/// The caller must free the client using `marmot_destroy_client`.
#[no_mangle]
pub extern "C" fn marmot_create_client(
    private_key_hex: *const c_char,
    public_key_hex: *const c_char,
) -> *mut MarmotClient {
    clear_last_error();

    let private_key = match unsafe { CStr::from_ptr(private_key_hex) }.to_str() {
        Ok(s) => s,
        Err(e) => {
            set_last_error(format!("Invalid private key string: {}", e));
            return ptr::null_mut();
        }
    };

    let public_key = match unsafe { CStr::from_ptr(public_key_hex) }.to_str() {
        Ok(s) => s,
        Err(e) => {
            set_last_error(format!("Invalid public key string: {}", e));
            return ptr::null_mut();
        }
    };

    match MarmotClient::new(private_key, public_key) {
        Ok(client) => Box::into_raw(Box::new(client)),
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Destroy a Marmot client and free its resources.
#[no_mangle]
pub extern "C" fn marmot_destroy_client(client: *mut MarmotClient) {
    if !client.is_null() {
        unsafe {
            drop(Box::from_raw(client));
        }
    }
}

/// Generate a new KeyPackage for group invitations.
///
/// # Returns
/// A pointer to the KeyPackage data, or null on failure.
/// The caller must free the buffer using `marmot_free_buffer`.
#[no_mangle]
pub extern "C" fn marmot_generate_key_package(
    client: *mut MarmotClient,
    data_length: *mut c_int,
) -> *mut u8 {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let client = unsafe { &mut *client };

    match client.generate_key_package() {
        Ok(data) => {
            unsafe { *data_length = data.len() as c_int };
            let boxed = data.into_boxed_slice();
            Box::into_raw(boxed) as *mut u8
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Create a new MLS group.
///
/// # Returns
/// A pointer to the group ID, or null on failure.
/// The caller must free the buffer using `marmot_free_buffer`.
#[no_mangle]
pub extern "C" fn marmot_create_group(
    client: *mut MarmotClient,
    group_name: *const c_char,
    group_id_length: *mut c_int,
    epoch: *mut u64,
) -> *mut u8 {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let name = match unsafe { CStr::from_ptr(group_name) }.to_str() {
        Ok(s) => s,
        Err(e) => {
            set_last_error(format!("Invalid group name: {}", e));
            return ptr::null_mut();
        }
    };

    let client = unsafe { &mut *client };

    match client.create_group(name) {
        Ok((group_id, group_epoch)) => {
            unsafe {
                *group_id_length = group_id.len() as c_int;
                *epoch = group_epoch;
            }
            let boxed = group_id.into_boxed_slice();
            Box::into_raw(boxed) as *mut u8
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Add a member to a group using their KeyPackage.
///
/// # Returns
/// A pointer to the Welcome message data, or null on failure.
/// The caller must free the buffer using `marmot_free_buffer`.
#[no_mangle]
pub extern "C" fn marmot_add_member(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    key_package_data: *const u8,
    key_package_length: c_int,
    welcome_length: *mut c_int,
) -> *mut u8 {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let key_package = unsafe { slice::from_raw_parts(key_package_data, key_package_length as usize) };

    let client = unsafe { &mut *client };

    match client.add_member(group_id, key_package) {
        Ok(welcome_data) => {
            unsafe { *welcome_length = welcome_data.len() as c_int };
            let boxed = welcome_data.into_boxed_slice();
            Box::into_raw(boxed) as *mut u8
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Process a Welcome message to join a group.
///
/// # Returns
/// A pointer to the group ID, or null on failure.
#[no_mangle]
pub extern "C" fn marmot_process_welcome(
    client: *mut MarmotClient,
    welcome_data: *const u8,
    welcome_length: c_int,
    group_id_length: *mut c_int,
    epoch: *mut u64,
    group_name: *mut *mut c_char,
    members_json: *mut *mut c_char,
) -> *mut u8 {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let welcome = unsafe { slice::from_raw_parts(welcome_data, welcome_length as usize) };
    let client = unsafe { &mut *client };

    match client.process_welcome(welcome) {
        Ok((group_id, name, group_epoch, members)) => {
            unsafe {
                *group_id_length = group_id.len() as c_int;
                *epoch = group_epoch;

                *group_name = CString::new(name).unwrap_or_default().into_raw();

                let members_str = serde_json::to_string(&members).unwrap_or_else(|_| "[]".to_string());
                *members_json = CString::new(members_str).unwrap_or_default().into_raw();
            }

            let boxed = group_id.into_boxed_slice();
            Box::into_raw(boxed) as *mut u8
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Encrypt a message for a group.
///
/// # Returns
/// A pointer to the ciphertext, or null on failure.
#[no_mangle]
pub extern "C" fn marmot_encrypt_message(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    plaintext: *const c_char,
    ciphertext_length: *mut c_int,
) -> *mut u8 {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let plaintext = match unsafe { CStr::from_ptr(plaintext) }.to_str() {
        Ok(s) => s,
        Err(e) => {
            set_last_error(format!("Invalid plaintext: {}", e));
            return ptr::null_mut();
        }
    };

    let client = unsafe { &mut *client };

    match client.encrypt_message(group_id, plaintext) {
        Ok(ciphertext) => {
            unsafe { *ciphertext_length = ciphertext.len() as c_int };
            let boxed = ciphertext.into_boxed_slice();
            Box::into_raw(boxed) as *mut u8
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Decrypt a message from a group.
///
/// # Returns
/// A pointer to the plaintext string, or null on failure.
#[no_mangle]
pub extern "C" fn marmot_decrypt_message(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    ciphertext: *const u8,
    ciphertext_length: c_int,
    sender_public_key: *mut *mut c_char,
    epoch: *mut u64,
) -> *mut c_char {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let ciphertext = unsafe { slice::from_raw_parts(ciphertext, ciphertext_length as usize) };

    let client = unsafe { &mut *client };

    match client.decrypt_message(group_id, ciphertext) {
        Ok((sender, plaintext, msg_epoch)) => {
            unsafe {
                *sender_public_key = CString::new(sender).unwrap_or_default().into_raw();
                *epoch = msg_epoch;
            }

            CString::new(plaintext).unwrap_or_default().into_raw()
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Process a commit message.
///
/// # Returns
/// 0 on success, non-zero on failure.
#[no_mangle]
pub extern "C" fn marmot_process_commit(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    commit_data: *const u8,
    commit_length: c_int,
) -> c_int {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return -1;
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let commit = unsafe { slice::from_raw_parts(commit_data, commit_length as usize) };

    let client = unsafe { &mut *client };

    match client.process_commit(group_id, commit) {
        Ok(_) => 0,
        Err(e) => {
            set_last_error(e);
            -1
        }
    }
}

/// Update keys for forward secrecy.
///
/// # Returns
/// A pointer to the commit data, or null on failure.
#[no_mangle]
pub extern "C" fn marmot_update_keys(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    commit_length: *mut c_int,
) -> *mut u8 {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let client = unsafe { &mut *client };

    match client.update_keys(group_id) {
        Ok(commit_data) => {
            unsafe { *commit_length = commit_data.len() as c_int };
            let boxed = commit_data.into_boxed_slice();
            Box::into_raw(boxed) as *mut u8
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Remove a member from a group.
///
/// # Returns
/// A pointer to the commit data, or null on failure.
#[no_mangle]
pub extern "C" fn marmot_remove_member(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    member_public_key: *const c_char,
    commit_length: *mut c_int,
) -> *mut u8 {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let member_key = match unsafe { CStr::from_ptr(member_public_key) }.to_str() {
        Ok(s) => s,
        Err(e) => {
            set_last_error(format!("Invalid member public key: {}", e));
            return ptr::null_mut();
        }
    };

    let client = unsafe { &mut *client };

    match client.remove_member(group_id, member_key) {
        Ok(commit_data) => {
            unsafe { *commit_length = commit_data.len() as c_int };
            let boxed = commit_data.into_boxed_slice();
            Box::into_raw(boxed) as *mut u8
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Get information about a group.
///
/// # Returns
/// 0 on success, non-zero if group not found.
#[no_mangle]
pub extern "C" fn marmot_get_group_info(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    group_name: *mut *mut c_char,
    epoch: *mut u64,
    members_json: *mut *mut c_char,
) -> c_int {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return -1;
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let client = unsafe { &*client };

    match client.get_group_info(group_id) {
        Some((name, group_epoch, members)) => {
            unsafe {
                *group_name = CString::new(name).unwrap_or_default().into_raw();
                *epoch = group_epoch;

                let members_str = serde_json::to_string(&members).unwrap_or_else(|_| "[]".to_string());
                *members_json = CString::new(members_str).unwrap_or_default().into_raw();
            }
            0
        }
        None => {
            set_last_error("Group not found");
            -1
        }
    }
}

/// Export group state for persistence.
///
/// # Returns
/// A pointer to the state data, or null on failure.
#[no_mangle]
pub extern "C" fn marmot_export_group_state(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    state_length: *mut c_int,
) -> *mut u8 {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return ptr::null_mut();
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let client = unsafe { &*client };

    match client.export_group_state(group_id) {
        Ok(state) => {
            unsafe { *state_length = state.len() as c_int };
            let boxed = state.into_boxed_slice();
            Box::into_raw(boxed) as *mut u8
        }
        Err(e) => {
            set_last_error(e);
            ptr::null_mut()
        }
    }
}

/// Import group state from persistence.
///
/// # Returns
/// 0 on success, non-zero on failure.
#[no_mangle]
pub extern "C" fn marmot_import_group_state(
    client: *mut MarmotClient,
    group_id: *const u8,
    group_id_length: c_int,
    state: *const u8,
    state_length: c_int,
) -> c_int {
    clear_last_error();

    if client.is_null() {
        set_last_error("Client is null");
        return -1;
    }

    let group_id = unsafe { slice::from_raw_parts(group_id, group_id_length as usize) };
    let state = unsafe { slice::from_raw_parts(state, state_length as usize) };

    let client = unsafe { &mut *client };

    match client.import_group_state(group_id, state) {
        Ok(_) => 0,
        Err(e) => {
            set_last_error(e);
            -1
        }
    }
}

/// Free a buffer allocated by this library.
#[no_mangle]
pub extern "C" fn marmot_free_buffer(buffer: *mut u8) {
    if !buffer.is_null() {
        unsafe {
            // We don't know the length, so we rely on Box's drop implementation
            // This is safe because we always allocate with Box::into_raw
            drop(Box::from_raw(buffer));
        }
    }
}

/// Free a string allocated by this library.
#[no_mangle]
pub extern "C" fn marmot_free_string(s: *mut c_char) {
    if !s.is_null() {
        unsafe {
            drop(CString::from_raw(s));
        }
    }
}
