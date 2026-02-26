//! Error types for the Marmot native library.

use thiserror::Error;

#[derive(Error, Debug)]
pub enum MarmotError {
    #[error("Invalid key format: {0}")]
    InvalidKey(String),

    #[error("Group not found: {0}")]
    GroupNotFound(String),

    #[error("MLS error: {0}")]
    MlsError(String),

    #[error("Serialization error: {0}")]
    SerializationError(String),

    #[error("Crypto error: {0}")]
    CryptoError(String),

    #[error("Invalid state: {0}")]
    InvalidState(String),

    #[error("Member not found: {0}")]
    MemberNotFound(String),

    #[error("Already a member")]
    AlreadyMember,

    #[error("Not a member of the group")]
    NotMember,

    #[error("Internal error: {0}")]
    Internal(String),
}

impl From<std::string::FromUtf8Error> for MarmotError {
    fn from(err: std::string::FromUtf8Error) -> Self {
        MarmotError::SerializationError(err.to_string())
    }
}

impl From<serde_json::Error> for MarmotError {
    fn from(err: serde_json::Error) -> Self {
        MarmotError::SerializationError(err.to_string())
    }
}

impl From<hex::FromHexError> for MarmotError {
    fn from(err: hex::FromHexError) -> Self {
        MarmotError::InvalidKey(err.to_string())
    }
}
