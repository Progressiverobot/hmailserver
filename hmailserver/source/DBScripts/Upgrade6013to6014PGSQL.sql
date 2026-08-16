create table hm_imap_metadata
(
   metadataid bigserial not null,
   metadataaccountid bigint not null,
   metadatafolderid bigint not null,
   metadataentryname varchar(255) not null,
   metadatavalue varchar(2048) not null,
   constraint hm_imap_metadata_pk primary key (metadataid),
   constraint hm_imap_metadata_unique unique (metadataaccountid, metadatafolderid, metadataentryname)
);

update hm_dbversion set value = 6014;
