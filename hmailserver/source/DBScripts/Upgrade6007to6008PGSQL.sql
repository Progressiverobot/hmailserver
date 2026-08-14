alter table hm_tcpipports add column if not exists portclientcertificatepolicy smallint not null default 0;

alter table hm_tcpipports add column if not exists portclientcertificatecafile varchar(255) not null default '';

update hm_dbversion set value = 6008;
