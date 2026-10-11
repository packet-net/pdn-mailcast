# Linux FBB 7.0.11 (xfbbd), from Ubuntu 24.04's fbb package, set up as docs/receiver.md tells a
# sysop to set one up for the receiver. Built by FbbContainer for the Category=Docker tests.
FROM ubuntu@sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55
# Retries, so that a slow Ubuntu mirror does not fail a release.
RUN apt-get -o Acquire::Retries=3 update \
    && DEBIAN_FRONTEND=noninteractive apt-get -o Acquire::Retries=3 install -y --no-install-recommends fbb=7.011-3 \
    && rm -rf /var/lib/apt/lists/*
COPY fbb.conf.head port.sys passwd.sys /etc/ax25/fbb/
# As the fbb start script's first-run set-up makes it: the mandatory lines, then the packaged
# optional ones.
RUN cd /etc/ax25/fbb && cat fbb.conf.head fbbopt.conf > fbb.conf && rm fbb.conf.head
COPY start.sh /usr/local/bin/start-fbb
COPY fbb-console /usr/local/bin/fbb-console
RUN chmod +x /usr/local/bin/fbb-console
WORKDIR /var/ax25/fbb
EXPOSE 6300
CMD ["bash", "/usr/local/bin/start-fbb"]
