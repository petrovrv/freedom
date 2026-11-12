# SPDX-License-Identifier: AGPL-3.0-or-later
# Freedom variants B/C (SPEC Freedom section 4): the "hosting" of variant A as one image - Apache + PHP 8.3 (mysqli,
# openssl, curl, mbstring, zlib), Node 24 for the site agent (co-agent/executor), the MariaDB client for the schema and
# backups. The image holds no AI Workbench code: install/freedom_docker.py deploy writes it into the volume /home/co with
# the same repo -> server map as variant A (freedom_setup.layout(), cPanel layout: ~/public_html, ~/co-agent, ~/co-auth).
# The same image runs the cron sidecar (freedom-cron.php).
ARG PHP_IMAGE=php:8.3-apache-bookworm
ARG NODE_IMAGE=node:24-bookworm-slim
FROM ${NODE_IMAGE} AS node

FROM ${PHP_IMAGE}
RUN apt-get update \
 && apt-get install -y --no-install-recommends mariadb-client ca-certificates openssl procps tar \
 && docker-php-ext-install mysqli \
 && apt-get clean && rm -rf /var/lib/apt/lists/* \
 && php -m | grep -qi '^mysqli$' && php -m | grep -qi '^mbstring$' && php -m | grep -qi '^curl$' \
 && php -m | grep -qi '^openssl$' && php -m | grep -qi '^zlib$'

# Node + npm (npm only for the optional CLIs of the site agent: freedom_docker.py deploy --parts ...,cli)
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/npm
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
 && node --version && npm --version

# The account of the "hosting": Apache, PHP, cron and the site agent all run as co (uid 1000), HOME=/home/co
ARG CO_UID=1000
RUN groupadd -g ${CO_UID} co && useradd -u ${CO_UID} -g co -d /home/co -s /bin/sh co \
 && mkdir -p /home/co/public_html /home/co/co-agent/bin /home/co/co-agent/state /home/co/co-auth \
 && chown -R co:co /home/co && chmod 711 /home/co
ENV HOME=/home/co \
    APACHE_RUN_USER=co \
    APACHE_RUN_GROUP=co \
    APACHE_DOCUMENT_ROOT=/home/co/public_html \
    NODE_EXTRA_CA_CERTS=/etc/ssl/freedom/app.crt

# Apache answers TLS itself on 443 (self-signed for the names "app" and "localhost"): the .htaccess of CO Web sends
# every plain-HTTP request to https, so the reverse proxy (Caddy) talks to https://app:443. The site agent calls db.php
# at https://app (config.php site_url) and trusts this certificate through NODE_EXTRA_CA_CERTS.
RUN mkdir -p /etc/ssl/freedom \
 && openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj "/CN=app" \
      -addext "subjectAltName=DNS:app,DNS:localhost,IP:127.0.0.1" \
      -keyout /etc/ssl/freedom/app.key -out /etc/ssl/freedom/app.crt \
 && chmod 600 /etc/ssl/freedom/app.key && chmod 644 /etc/ssl/freedom/app.crt \
 && a2enmod rewrite headers ssl \
 && a2dissite 000-default \
 && mkdir -p /var/www/health && echo ok > /var/www/health/freedom-health
COPY apache-site.conf /etc/apache2/sites-available/freedom.conf
COPY php.ini /usr/local/etc/php/conf.d/freedom.ini
COPY freedom-cron.php /usr/local/lib/freedom/freedom-cron.php
COPY run-php-tests.sh /usr/local/lib/freedom/run-php-tests.sh
RUN a2ensite freedom && chmod 755 /usr/local/lib/freedom/run-php-tests.sh && apache2ctl configtest

EXPOSE 80 443
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD php -r 'exit(@file_get_contents("http://127.0.0.1/freedom-health") === false ? 1 : 0);'
