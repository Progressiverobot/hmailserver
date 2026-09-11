# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# https://www.progressiverobot.com
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# The hMailServer container image: the Linux .deb the tag's own workflow built,
# installed on the distribution release it was built for, run as its own user.
#
#   docker run -d --name hmailserver \
#      -e HM_DB_TYPE=PostgreSQL -e HM_DB_HOST=db -e HM_DB_NAME=hmailserver \
#      -e HM_DB_USER=hmailserver -e HM_DB_PASSWORD=... \
#      -e HM_ADMIN_PASSWORD=... \
#      -e HM_REST_PORT=8045 -e HM_REST_BIND_ADDRESS=0.0.0.0 \
#      -e HM_REST_CERTIFICATE_FILE=/certs/rest.pem -e HM_REST_PRIVATE_KEY_FILE=/certs/rest.key \
#      -v /srv/hmailserver/certs:/certs:ro \
#      -p 25:25 -p 587:587 -p 993:993 -p 8045:8045 \
#      -v hm-data:/var/lib/hmailserver -v hm-logs:/var/log/hmailserver -v hm-etc:/etc/hmailserver \
#      ghcr.io/progressiverobot/hmailserver:<version>
#
# The database is not in the image and never will be: point HM_DB_* at a
# PostgreSQL or MariaDB server. On first start the entrypoint writes the
# [Database] section from those variables, the REST listener from HM_REST_*, the
# administrator password as a hash through the server's own --set-admin-password,
# creates the database if it does not exist, upgrades it if it is behind, and
# then runs the server in the foreground. On Linux the REST API is the
# administration (there is no Control Panel and no COM), and the server requires
# TLS on it unless it is bound to loopback - which inside a container is the
# container's own loopback - so a reachable API means a certificate and key
# mounted in, or host networking. See docker-entrypoint.sh for every variable.
# Ubuntu 24.04 is the base because the package's dependencies are the sonames of
# the runner that built it (see the roadmap's Linux section); a different base
# would refuse the .deb.

FROM ubuntu:24.04@sha256:224a1869083a311ef3f13648a154ba79832fbef6364d31493642ca03082da254

ARG HM_DEB=packages/hmailserver_*_amd64.deb

# The package's post-install script creates the hmailserver user and the data,
# log and configuration directories, and is written to run where there is no
# systemd. libcap2-bin is for setcap below; ca-certificates for the TLS the
# server does as a client (MTA-STS, ACME, outbound SMTP).
COPY ${HM_DEB} /tmp/hmailserver.deb
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      /tmp/hmailserver.deb ca-certificates libcap2-bin \
 && rm -rf /var/lib/apt/lists/* /tmp/hmailserver.deb \
 # Ports 25, 110, 143, 465, 587, 993 and 995 are below 1024 and the process does
 # not run as root: the one capability it needs, on the binary rather than on
 # the container.
 && setcap cap_net_bind_service=+ep /usr/bin/hmailserver

COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 0755 /usr/local/bin/docker-entrypoint.sh \
 && chown -R hmailserver:hmailserver /etc/hmailserver

VOLUME ["/var/lib/hmailserver", "/var/log/hmailserver", "/etc/hmailserver"]
EXPOSE 25 110 143 465 587 993 995 4190 8045

USER hmailserver
WORKDIR /var/lib/hmailserver

# Alive means listening: SMTP accepts a connection. No curl in the image; bash
# opens the socket itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/25 && exec 3>&-' || exit 1

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["hmailserver", "--foreground"]
