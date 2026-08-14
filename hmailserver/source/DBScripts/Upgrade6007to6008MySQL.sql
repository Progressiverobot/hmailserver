alter table hm_tcpipports add portclientcertificatepolicy tinyint not null default 0 /* [IGNORE-ERRORS] */;

alter table hm_tcpipports add portclientcertificatecafile varchar(255) not null default '' /* [IGNORE-ERRORS] */;

update hm_dbversion set value = 6008;
