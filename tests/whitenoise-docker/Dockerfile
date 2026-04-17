# Multi-stage build for whitenoise-rs CLI + daemon
# Stage 1: Build from source + custom test binary with mock keyring
FROM rust:1.90-bookworm AS builder

RUN apt-get update && apt-get install -y \
    pkg-config \
    libssl-dev \
    libsqlite3-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /build
RUN git clone --depth 1 https://github.com/marmot-protocol/whitenoise-rs.git .

# Build the standard CLI binaries
RUN cargo build --release --features cli

# Build a custom test daemon that uses mock keyring (no OS keyring needed)
COPY wnd_test.rs /build/src/bin/wnd_test.rs
RUN echo '\n[[bin]]\nname = "wnd_test"\npath = "src/bin/wnd_test.rs"\nrequired-features = ["cli"]' >> Cargo.toml
RUN cargo build --release --features cli,integration-tests --bin wnd_test

# Stage 2: Runtime image
FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y \
    ca-certificates \
    libssl3 \
    libsqlite3-0 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /build/target/release/wnd /usr/local/bin/wnd
COPY --from=builder /build/target/release/wnd_test /usr/local/bin/wnd_test
COPY --from=builder /build/target/release/wn /usr/local/bin/wn

RUN mkdir -p /data /logs

ENV RUST_LOG=info,whitenoise=debug,mdk_core=debug

# Use the test daemon with mock keyring (no OS keyring needed in containers)
ENTRYPOINT ["wnd_test", "--data-dir", "/data", "--logs-dir", "/logs"]
