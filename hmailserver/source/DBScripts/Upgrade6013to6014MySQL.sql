create table hm_imap_metadata
(
   metadataid bigint not null auto_increment,
   metadataaccountid bigint not null,
   metadatafolderid bigint not null,
   metadataentryname varchar(255) not null,
   metadatavalue varchar(2048) not null,
   primary key (metadataid),
   unique key hm_imap_metadata_unique (metadataaccountid, metadatafolderid, metadataentryname)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4;

update hm_dbversion set value = 6014;
