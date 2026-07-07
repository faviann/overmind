-- Dev/CI provisioning: mirrors what Ansible does in production.
-- The memsrv login role exists BEFORE migrations run; migrations grant to it
-- but never create roles or manage passwords.
CREATE ROLE memsrv LOGIN PASSWORD 'memsrv_dev';
CREATE DATABASE memory_dev;
CREATE DATABASE memory_test;
