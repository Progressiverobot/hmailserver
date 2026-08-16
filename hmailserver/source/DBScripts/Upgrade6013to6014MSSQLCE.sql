create table hm_imap_metadata
(
   metadataid bigint identity(1,1) not null,
   metadataaccountid bigint not null,
   metadatafolderid bigint not null,
   metadataentryname nvarchar(255) not null,
   metadatavalue nvarchar(2048) not null
)

ALTER TABLE hm_imap_metadata ADD CONSTRAINT hm_imap_metadata_pk PRIMARY KEY (metadataid)

ALTER TABLE hm_imap_metadata ADD CONSTRAINT hm_imap_metadata_unique UNIQUE (metadataaccountid, metadatafolderid, metadataentryname)

update hm_dbversion set value = 6014
